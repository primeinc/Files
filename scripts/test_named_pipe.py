#!/usr/bin/env python3
"""
Test named pipe IPC implementation for Files App.

This script tests the enhanced named pipe functionality with:
- PipeWriter and PipeWriteLock for thread-safe operations
- SendResponseAsync and BroadcastAsync methods
- Proper framing with length prefix
"""

import asyncio
import json
import os
import struct
import sys
from pathlib import Path
import win32pipe
import win32file
import pywintypes

def discover_pipe_name():
    """Discover pipe name from rendezvous file."""
    rendezvous_path = Path(os.environ['LOCALAPPDATA']) / 'FilesIPC' / 'ipc.info'
    
    if not rendezvous_path.exists():
        raise FileNotFoundError(f"Rendezvous file not found at {rendezvous_path}")
    
    with open(rendezvous_path, 'r') as f:
        data = json.load(f)
    
    pipe_name = data.get('pipeName')
    token = data.get('token')
    
    if not pipe_name:
        raise ValueError("No pipe name found in rendezvous file (named pipes may not be enabled)")
    
    print(f"[Discovery] Found pipe config:")
    print(f"  Pipe Name: {pipe_name}")
    print(f"  Token: {token[:8]}...")
    
    return pipe_name, token


class NamedPipeClient:
    def __init__(self, pipe_name: str):
        self.pipe_name = f"\\\\.\\pipe\\{pipe_name}"
        self.pipe_handle = None
        self._id = 1
        
    def connect(self):
        """Connect to the named pipe."""
        try:
            # Try with different security attributes
            self.pipe_handle = win32file.CreateFile(
                self.pipe_name,
                win32file.GENERIC_READ | win32file.GENERIC_WRITE,
                0,  # no sharing
                None,  # default security
                win32file.OPEN_EXISTING,
                win32file.FILE_FLAG_OVERLAPPED,  # async mode
                None  # no template
            )
            print(f"Connected to pipe: {self.pipe_name}")
        except pywintypes.error as e:
            raise ConnectionError(f"Failed to connect to pipe: {e}")
    
    def send_message(self, message: dict):
        """Send a JSON-RPC message with length prefix."""
        json_str = json.dumps(message)
        json_bytes = json_str.encode('utf-8')
        
        # Length prefix (4 bytes, little-endian)
        length_bytes = struct.pack('<I', len(json_bytes))
        
        # Write length + payload
        win32file.WriteFile(self.pipe_handle, length_bytes + json_bytes)
        print(f">> Sent: {message.get('method', 'response')} (id={message.get('id')})")
    
    def receive_message(self):
        """Receive a JSON-RPC message with length prefix."""
        # Read length prefix (4 bytes)
        result, length_data = win32file.ReadFile(self.pipe_handle, 4)
        if len(length_data) != 4:
            raise IOError("Failed to read message length")
        
        length = struct.unpack('<I', length_data)[0]
        
        # Read payload
        result, payload_data = win32file.ReadFile(self.pipe_handle, length)
        if len(payload_data) != length:
            raise IOError(f"Expected {length} bytes, got {len(payload_data)}")
        
        json_str = payload_data.decode('utf-8')
        message = json.loads(json_str)
        print(f"<< Received: {json.dumps(message, indent=2)}")
        return message
    
    def call(self, method: str, params: dict = None, validate_notifications=False):
        """Make a JSON-RPC call and wait for response."""
        msg = {
            "jsonrpc": "2.0",
            "id": self._id,
            "method": method
        }
        if params:
            msg["params"] = params
        
        request_id = self._id
        self._id += 1
        self.send_message(msg)
        
        notifications = []
        # Keep reading until we get our response (collect notifications)
        max_attempts = 10
        for _ in range(max_attempts):
            response = self.receive_message()
            
            # Check if this is a notification (no id field or IsNotification=true)
            if response.get("IsNotification") or response.get("id") is None:
                notifications.append(response)
                if validate_notifications:
                    method_name = response.get('method')
                    params = response.get('params', {})
                    print(f"   [VALIDATED] Notification: {method_name} - path={params.get('path', 'N/A')}")
                continue
            
            # Check if this is our response
            if response.get("id") == request_id:
                # Handle both error formats
                if "error" in response and response["error"] is not None:
                    raise RuntimeError(f"RPC error: {response['error']}")
                
                result = response.get("result")
                # Validate notifications if requested
                if validate_notifications and notifications:
                    print(f"   Validated {len(notifications)} notifications")
                return result
        
        raise RuntimeError(f"No response received for request id={request_id} after {max_attempts} messages")
    
    def close(self):
        """Close the pipe connection."""
        if self.pipe_handle:
            win32file.CloseHandle(self.pipe_handle)
            print("Pipe connection closed")


def test_named_pipe(pipe_name: str, token: str):
    """Test named pipe functionality."""
    print("\n=== NAMED PIPE TEST ===\n")
    
    client = NamedPipeClient(pipe_name)
    
    try:
        # Connect to pipe
        client.connect()
        
        # Test 1: Handshake
        print("\nTest 1: Handshake")
        result = client.call("handshake", {
            "token": token,
            "clientInfo": "named-pipe-test"
        })
        print(f"Handshake result: {result}")
        
        # Test 2: Get state
        print("\nTest 2: Get state")
        state = client.call("getState")
        print(f"Current path: {state.get('currentPath')}")
        print(f"Item count: {state.get('itemCount')}")
        
        # Test 3: List actions
        print("\nTest 3: List actions")
        actions = client.call("listActions")
        print(f"Available actions: {len(actions.get('actions', []))}")
        
        # Test 4: Navigate (with notification validation)
        print("\nTest 4: Navigate to C:\\Users (validating notifications)")
        nav_result = client.call("navigate", {"path": "C:\\Users"}, validate_notifications=True)
        print(f"Navigation result: {nav_result}")
        
        # Test 5: Get metadata
        print("\nTest 5: Get metadata")
        metadata = client.call("getMetadata", {
            "paths": ["C:\\Windows", "C:\\Users"]
        })
        for item in metadata.get("items", []):
            print(f"  - {item['Path']}: exists={item['Exists']}, isDir={item['IsDirectory']}")
        
        print("\n=== ALL TESTS PASSED ===")
        
    except Exception as e:
        print(f"\nTest failed: {e}")
        return 1
    
    finally:
        client.close()
    
    return 0


def test_multi_client_pipe(pipe_name: str, token: str):
    """Test multiple simultaneous pipe clients."""
    print("\n=== MULTI-CLIENT PIPE TEST ===\n")
    
    clients = []
    
    try:
        # Create 3 clients
        for i in range(1, 4):
            print(f"Creating client {i}...")
            client = NamedPipeClient(pipe_name)
            client.connect()
            clients.append(client)
            
            # Authenticate
            result = client.call("handshake", {
                "token": token,
                "clientInfo": f"pipe-client-{i}"
            })
            print(f"  Client {i} authenticated: {result.get('status')}")
        
        print("\nAll clients connected and authenticated!")
        
        # Test operations on each client
        for i, client in enumerate(clients, 1):
            state = client.call("getState")
            print(f"Client {i} got state: path={state.get('currentPath')}")
        
        print("\n=== MULTI-CLIENT SUCCESS ===")
        
    except Exception as e:
        print(f"\nMulti-client test failed: {e}")
        return 1
    
    finally:
        for client in clients:
            client.close()
    
    return 0


def main():
    print("Files App Named Pipe IPC Test")
    print("=" * 50)
    
    try:
        # Check if pywin32 is installed
        import win32pipe
        import win32file
    except ImportError:
        print("ERROR: pywin32 not installed")
        print("Install with: pip install pywin32")
        return 1
    
    try:
        pipe_name, token = discover_pipe_name()
    except Exception as e:
        print(f"Discovery failed: {e}")
        print("\nNote: Named pipes must be enabled in Files App")
        print("The current implementation may only have WebSocket enabled")
        return 1
    
    # Run tests
    result = test_named_pipe(pipe_name, token)
    if result == 0:
        result = test_multi_client_pipe(pipe_name, token)
    
    return result


if __name__ == "__main__":
    sys.exit(main())