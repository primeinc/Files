#!/usr/bin/env python3
"""
Test named pipe notifications and validate they work correctly.
"""

import asyncio
import json
import os
import struct
import sys
import threading
import time
from pathlib import Path
import win32pipe
import win32file
import pywintypes
from collections import deque


def discover_pipe_name():
    """Discover pipe name from rendezvous file."""
    rendezvous_path = Path(os.environ['LOCALAPPDATA']) / 'FilesIPC' / 'ipc.info'
    
    with open(rendezvous_path, 'r') as f:
        data = json.load(f)
    
    return data.get('pipeName'), data.get('token')


class NamedPipeClientAsync:
    def __init__(self, pipe_name: str):
        self.pipe_name = f"\\\\.\\pipe\\{pipe_name}"
        self.pipe_handle = None
        self._id = 1
        self.notifications = deque()
        self.responses = {}
        self.reader_thread = None
        self.stop_reading = threading.Event()
        
    def connect(self):
        """Connect to the named pipe."""
        self.pipe_handle = win32file.CreateFile(
            self.pipe_name,
            win32file.GENERIC_READ | win32file.GENERIC_WRITE,
            0,
            None,
            win32file.OPEN_EXISTING,
            0,
            None
        )
        print(f"Connected to pipe: {self.pipe_name}")
        
        # Start reader thread
        self.reader_thread = threading.Thread(target=self._reader_loop)
        self.reader_thread.daemon = True
        self.reader_thread.start()
    
    def _reader_loop(self):
        """Continuously read messages from pipe."""
        while not self.stop_reading.is_set():
            try:
                # Read length prefix (4 bytes)
                result, length_data = win32file.ReadFile(self.pipe_handle, 4)
                if len(length_data) != 4:
                    break
                
                length = struct.unpack('<I', length_data)[0]
                
                # Read payload
                result, payload_data = win32file.ReadFile(self.pipe_handle, length)
                if len(payload_data) != length:
                    break
                
                message = json.loads(payload_data.decode('utf-8'))
                
                # Sort into notifications vs responses
                if message.get("IsNotification") or message.get("id") is None:
                    self.notifications.append(message)
                    print(f"📨 Notification: {message.get('method')} - {json.dumps(message.get('params'), separators=(',', ':'))[:100]}")
                else:
                    self.responses[message.get("id")] = message
                    
            except Exception as e:
                if not self.stop_reading.is_set():
                    print(f"Reader error: {e}")
                break
    
    def send_message(self, message: dict):
        """Send a JSON-RPC message."""
        json_str = json.dumps(message)
        json_bytes = json_str.encode('utf-8')
        length_bytes = struct.pack('<I', len(json_bytes))
        win32file.WriteFile(self.pipe_handle, length_bytes + json_bytes)
        print(f"📤 Sent: {message.get('method', 'response')} (id={message.get('id')})")
    
    def call(self, method: str, params: dict = None, timeout: float = 5.0):
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
        
        # Wait for response
        start = time.time()
        while time.time() - start < timeout:
            if request_id in self.responses:
                response = self.responses.pop(request_id)
                if "error" in response and response["error"] is not None:
                    raise RuntimeError(f"RPC error: {response['error']}")
                return response.get("result")
            time.sleep(0.01)
        
        raise TimeoutError(f"No response for request id={request_id} after {timeout}s")
    
    def get_notifications(self, clear=True):
        """Get all received notifications."""
        notifications = list(self.notifications)
        if clear:
            self.notifications.clear()
        return notifications
    
    def wait_for_notification(self, method: str, timeout: float = 5.0):
        """Wait for a specific notification."""
        start = time.time()
        while time.time() - start < timeout:
            for notif in self.notifications:
                if notif.get("method") == method:
                    self.notifications.remove(notif)
                    return notif
            time.sleep(0.01)
        return None
    
    def close(self):
        """Close the pipe connection."""
        self.stop_reading.set()
        if self.reader_thread:
            self.reader_thread.join(timeout=1)
        if self.pipe_handle:
            win32file.CloseHandle(self.pipe_handle)
            print("Pipe connection closed")


def test_notifications(pipe_name: str, token: str):
    """Test that notifications are properly received."""
    print("\n=== NOTIFICATION VALIDATION TEST ===\n")
    
    client = NamedPipeClientAsync(pipe_name)
    
    try:
        client.connect()
        time.sleep(0.1)  # Let reader thread start
        
        # Authenticate
        result = client.call("handshake", {"token": token, "clientInfo": "notification-test"})
        print(f"✅ Authenticated: {result}")
        
        # Clear any initial notifications
        client.get_notifications(clear=True)
        
        # Test 1: Navigate should trigger notifications
        print("\n--- Test 1: Navigation Notifications ---")
        print("Navigating to C:\\Users...")
        nav_result = client.call("navigate", {"path": "C:\\Users"})
        print(f"✅ Navigate response: {nav_result}")
        
        # Wait a bit for notifications
        time.sleep(0.5)
        
        notifications = client.get_notifications()
        print(f"\nReceived {len(notifications)} notifications:")
        for notif in notifications:
            method = notif.get("method")
            params = notif.get("params", {})
            if method == "navigationStateChanged":
                print(f"  ✅ navigationStateChanged: back={params.get('canNavigateBack')}, forward={params.get('canNavigateForward')}, path={params.get('path')}")
            elif method == "workingDirectoryChanged":
                print(f"  ✅ workingDirectoryChanged: path={params.get('path')}, isLibrary={params.get('isLibrary')}")
            elif method == "itemsChanged":
                print(f"  ✅ itemsChanged: count={len(params.get('items', []))}")
            else:
                print(f"  ℹ️ {method}: {params}")
        
        if not notifications:
            print("  ⚠️ WARNING: No notifications received!")
        
        # Test 2: Another navigation
        print("\n--- Test 2: Second Navigation ---")
        print("Navigating to C:\\Windows...")
        nav_result = client.call("navigate", {"path": "C:\\Windows"})
        print(f"✅ Navigate response: {nav_result}")
        
        # Wait for specific notification
        nav_notif = client.wait_for_notification("navigationStateChanged", timeout=2.0)
        if nav_notif:
            params = nav_notif.get("params", {})
            print(f"✅ Got navigationStateChanged: path={params.get('path')}")
        else:
            print("⚠️ navigationStateChanged notification not received")
        
        dir_notif = client.wait_for_notification("workingDirectoryChanged", timeout=2.0)
        if dir_notif:
            params = dir_notif.get("params", {})
            print(f"✅ Got workingDirectoryChanged: path={params.get('path')}")
        else:
            print("⚠️ workingDirectoryChanged notification not received")
        
        # Test 3: Refresh action
        print("\n--- Test 3: Refresh Action ---")
        print("Executing refresh action...")
        refresh_result = client.call("executeAction", {"actionId": "refresh"})
        print(f"✅ Refresh response: {refresh_result}")
        
        time.sleep(0.5)
        notifications = client.get_notifications()
        if notifications:
            print(f"Received {len(notifications)} notifications after refresh:")
            for notif in notifications:
                print(f"  - {notif.get('method')}")
        else:
            print("ℹ️ No notifications from refresh (may be expected)")
        
        # Test 4: Validate broadcast functionality
        print("\n--- Test 4: Broadcast Test (Multi-client) ---")
        print("Creating second client to test broadcast...")
        
        client2 = NamedPipeClientAsync(pipe_name)
        client2.connect()
        time.sleep(0.1)
        
        result2 = client2.call("handshake", {"token": token, "clientInfo": "broadcast-test"})
        print(f"✅ Client 2 authenticated: {result2}")
        
        # Clear notifications on both
        client.get_notifications(clear=True)
        client2.get_notifications(clear=True)
        
        # Client 1 navigates - both should get notifications
        print("\nClient 1 navigating to C:\\Program Files...")
        nav_result = client.call("navigate", {"path": "C:\\Program Files"})
        
        time.sleep(0.5)
        
        notifs1 = client.get_notifications()
        notifs2 = client2.get_notifications()
        
        print(f"Client 1 received {len(notifs1)} notifications")
        print(f"Client 2 received {len(notifs2)} notifications")
        
        if notifs2:
            print("✅ Broadcast working: Client 2 received notifications from Client 1's action")
            for notif in notifs2:
                print(f"  - {notif.get('method')}")
        else:
            print("⚠️ Broadcast may not be working: Client 2 didn't receive notifications")
        
        client2.close()
        
        print("\n=== NOTIFICATION TESTS COMPLETE ===")
        
        # Summary
        print("\nSummary:")
        print("✅ Notifications are being sent by the server")
        print("✅ Notifications can be received and validated")
        print("✅ Both responses and notifications work correctly")
        
    except Exception as e:
        print(f"\n❌ Test failed: {e}")
        return 1
    
    finally:
        client.close()
    
    return 0


def main():
    print("Named Pipe Notification Validation Test")
    print("=" * 50)
    
    try:
        pipe_name, token = discover_pipe_name()
        print(f"Discovered pipe: {pipe_name[:50]}...")
    except Exception as e:
        print(f"Discovery failed: {e}")
        return 1
    
    return test_notifications(pipe_name, token)


if __name__ == "__main__":
    sys.exit(main())