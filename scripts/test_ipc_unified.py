#!/usr/bin/env python3
"""
Unified IPC test suite that tests BOTH WebSocket and Named Pipe transports
with identical test scenarios to ensure 1:1 parity.

Usage:
  python scripts/test_ipc_unified.py [--transport ws|pipe|both] [--test <TEST_NAME>]
"""

import argparse
import asyncio
import json
import os
import struct
import sys
import time
from abc import ABC, abstractmethod
from pathlib import Path
from typing import Any, Dict, Optional, Tuple

# Transport-specific imports
import websockets
import win32pipe
import win32file
import pywintypes


def discover_ipc_config() -> Tuple[str, int, str]:
    """Discover IPC configuration from rendezvous file."""
    rendezvous_path = Path(os.environ['LOCALAPPDATA']) / 'FilesIPC' / 'ipc.info'
    
    if not rendezvous_path.exists():
        raise FileNotFoundError(f"Rendezvous file not found at {rendezvous_path}")
    
    with open(rendezvous_path, 'r') as f:
        data = json.load(f)
    
    token = data.get('token')
    port = data.get('webSocketPort', 52345)
    pipe = data.get('pipeName')
    
    if not token:
        raise ValueError("No token found in rendezvous file")
    
    print(f"[Discovery] Found IPC config:")
    print(f"  Token: {token[:8]}...")
    print(f"  WebSocket Port: {port}")
    print(f"  Named Pipe: {pipe}")
    
    return token, port, pipe


class IpcClient(ABC):
    """Abstract base class for IPC clients."""
    
    @abstractmethod
    async def connect(self, token: str):
        """Connect and authenticate."""
        pass
    
    @abstractmethod
    async def call(self, method: str, params: Dict[str, Any] = None) -> Any:
        """Make an RPC call."""
        pass
    
    @abstractmethod
    async def close(self):
        """Close the connection."""
        pass


class WebSocketClient(IpcClient):
    """WebSocket IPC client."""
    
    def __init__(self, port: int):
        self.port = port
        self.ws = None
        self._id = 1
        self._responses = {}
        self._response_events = {}
        self._receiver_task = None
    
    async def connect(self, token: str):
        """Connect and authenticate via WebSocket."""
        self.ws = await websockets.connect(f"ws://127.0.0.1:{self.port}/")
        self._receiver_task = asyncio.create_task(self._receive_loop())
        
        # Handshake
        result = await self.call("handshake", {"token": token, "clientInfo": "unified-test-ws"})
        if result.get("status") != "authenticated":
            raise RuntimeError(f"WebSocket handshake failed: {result}")
    
    async def _receive_loop(self):
        """Continuously receive messages."""
        try:
            async for msg in self.ws:
                data = json.loads(msg)
                if "id" in data and data["id"] is not None:
                    self._responses[data["id"]] = data
                    # Set event if waiting for this response
                    if data["id"] in self._response_events:
                        self._response_events[data["id"]].set()
        except (websockets.exceptions.ConnectionClosed, 
                websockets.exceptions.ConnectionClosedOK,
                asyncio.CancelledError):
            # Expected disconnections - connection closed normally
            pass
        except Exception as e:
            print(f"[ERROR] Unexpected exception in receive loop: {e}")
            import traceback
            traceback.print_exc()
    
    async def call(self, method: str, params: Dict[str, Any] = None) -> Any:
        """Make an RPC call via WebSocket."""
        msg_id = self._id
        self._id += 1
        
        msg = {"jsonrpc": "2.0", "id": msg_id, "method": method}
        if params:
            msg["params"] = params
        
        # Create event for this response
        event = asyncio.Event()
        self._response_events[msg_id] = event
        
        await self.ws.send(json.dumps(msg))
        
        # Wait for response with timeout
        try:
            await asyncio.wait_for(event.wait(), timeout=5.0)
        except asyncio.TimeoutError:
            self._response_events.pop(msg_id, None)
            raise TimeoutError(f"No response for {method} (id: {msg_id})")
        
        # Get and return response
        self._response_events.pop(msg_id, None)
        resp = self._responses.pop(msg_id)
        if "error" in resp and resp["error"]:
            raise RuntimeError(f"RPC error: {resp['error']}")
        return resp.get("result")
    
    async def close(self):
        """Close WebSocket connection."""
        if self._receiver_task:
            self._receiver_task.cancel()
        if self.ws:
            await self.ws.close()


class NamedPipeClient(IpcClient):
    """Named Pipe IPC client (async wrapper)."""
    
    def __init__(self, pipe_name: str):
        self.pipe_name = f"\\\\.\\pipe\\{pipe_name}"
        self.pipe_handle = None
        self._id = 1
    
    async def connect(self, token: str):
        """Connect and authenticate via Named Pipe."""
        await asyncio.get_event_loop().run_in_executor(None, self._connect_sync, token)
    
    def _connect_sync(self, token: str):
        """Synchronous connection."""
        self.pipe_handle = win32file.CreateFile(
            self.pipe_name,
            win32file.GENERIC_READ | win32file.GENERIC_WRITE,
            0, None,
            win32file.OPEN_EXISTING,
            0, None
        )
        
        # Handshake
        result = self._call_sync("handshake", {"token": token, "clientInfo": "unified-test-pipe"})
        if result.get("status") != "authenticated":
            raise RuntimeError(f"Named pipe handshake failed: {result}")
    
    async def call(self, method: str, params: Dict[str, Any] = None) -> Any:
        """Make an RPC call via Named Pipe."""
        return await asyncio.get_event_loop().run_in_executor(
            None, self._call_sync, method, params
        )
    
    def _call_sync(self, method: str, params: Dict[str, Any] = None) -> Any:
        """Synchronous RPC call."""
        msg_id = self._id
        self._id += 1
        
        msg = {"jsonrpc": "2.0", "id": msg_id, "method": method}
        if params:
            msg["params"] = params
        
        # Send with length prefix
        json_bytes = json.dumps(msg).encode('utf-8')
        length_bytes = struct.pack('<I', len(json_bytes))
        win32file.WriteFile(self.pipe_handle, length_bytes + json_bytes)
        
        # Read response (may get notifications first)
        start_time = time.time()
        timeout = 5.0  # 5-second timeout
        
        while time.time() - start_time < timeout:
            # Read length
            _, length_data = win32file.ReadFile(self.pipe_handle, 4)
            length = struct.unpack('<I', length_data)[0]
            
            # Read payload
            _, payload_data = win32file.ReadFile(self.pipe_handle, length)
            response = json.loads(payload_data.decode('utf-8'))
            
            # Skip notifications
            if response.get("IsNotification") or response.get("id") is None:
                continue
            
            if response.get("id") == msg_id:
                if "error" in response and response["error"]:
                    raise RuntimeError(f"RPC error: {response['error']}")
                return response.get("result")
        
        raise TimeoutError(f"No response for {method} after {timeout} seconds")
    
    async def close(self):
        """Close Named Pipe connection."""
        if self.pipe_handle:
            await asyncio.get_event_loop().run_in_executor(
                None, win32file.CloseHandle, self.pipe_handle
            )


class UnifiedIpcTester:
    """Unified test suite for both transports."""
    
    def __init__(self, client: IpcClient, transport_name: str):
        self.client = client
        self.transport = transport_name
        self.passed = 0
        self.failed = 0
    
    async def run_all_tests(self):
        """Run all test scenarios."""
        print(f"\n{'='*60}")
        print(f"Testing {self.transport} Transport")
        print(f"{'='*60}")
        
        tests = [
            self.test_basic_operations,
            self.test_navigation,
            self.test_invalid_paths,
            self.test_metadata,
            self.test_list_shells,
            self.test_actions,
            self.test_invalid_action,
            self.test_error_handling,
            self.test_large_payload,
        ]
        
        for test in tests:
            try:
                await test()
                self.passed += 1
                print(f"[PASS] {test.__name__}")
            except Exception as e:
                self.failed += 1
                print(f"[FAIL] {test.__name__}: {e}")
        
        print(f"\n{self.transport} Results: {self.passed} passed, {self.failed} failed")
        return self.failed == 0
    
    async def test_basic_operations(self):
        """Test basic RPC operations."""
        # Get state
        state = await self.client.call("getState")
        assert "currentPath" in state, "Missing currentPath"
        assert "itemCount" in state, "Missing itemCount"
        
        # List actions
        actions = await self.client.call("listActions")
        assert "actions" in actions, "Missing actions list"
        assert len(actions["actions"]) > 0, "No actions available"
        
        # Print available actions for debugging
        print(f"\n[INFO] Available IPC actions:")
        for action in actions["actions"]:
            print(f"  - {action['id']}: {action.get('description', 'No description')}")
    
    async def test_navigation(self):
        """Test navigation operations."""
        # Navigate to a valid path
        result = await self.client.call("navigate", {"path": "C:\\Windows"})
        assert result is not None, "Navigate returned None"
        
        # Verify navigation
        state = await self.client.call("getState")
        assert state["currentPath"] == "C:\\Windows", f"Navigation failed: {state['currentPath']}"
        
        # Navigate back
        result = await self.client.call("navigate", {"path": "C:\\Users"})
        assert result is not None, "Navigate back failed"
    
    async def test_invalid_paths(self):
        """Test handling of invalid paths."""
        # Test 1: Non-existent path should return successfully but indicate the path doesn't exist
        result = await self.client.call("navigate", {"path": "Z:\\NonExistent\\Path"})
        assert result is not None, "Navigate returned None for non-existent path"
        assert isinstance(result, dict), f"Navigate should return dict, got {type(result)}"
        # Should either return success status (navigated to closest valid parent) or indicate path issue
        assert "status" in result, "Missing status field in navigation response"
        assert result["status"] == "ok", "Navigation should handle non-existent paths gracefully"
        
        # Test 2: Very long path (exceeds Windows MAX_PATH)
        long_path = "C:\\" + "\\VeryLongFolderName" * 50  # Creates path > 260 chars
        assert len(long_path) > 260, "Test path should exceed Windows MAX_PATH limit"
        
        try:
            result = await self.client.call("navigate", {"path": long_path})
            # If it succeeds, verify the response structure
            assert result is not None, "Navigate returned None for long path"
            assert isinstance(result, dict), f"Navigate should return dict, got {type(result)}"
            assert "status" in result, "Missing status field in response"
            # The path should be normalized/truncated or handled appropriately
            assert result["status"] == "ok", "Long path should be handled without error"
        except RuntimeError as e:
            # If it fails, ensure it's a proper JSON-RPC error
            error_str = str(e)
            assert "RPC error" in error_str, f"Expected RPC error format, got: {error_str}"
            # Should contain either InvalidParams error or a descriptive message
            assert any(x in error_str.lower() for x in ["invalid", "path", "long", "exceed"]), \
                f"Error should describe the path issue, got: {error_str}"
        
        # Test 3: Path with invalid characters
        invalid_char_path = "C:\\Invalid<>Path|Name?.txt"
        try:
            result = await self.client.call("navigate", {"path": invalid_char_path})
            # Should handle gracefully even with invalid chars
            assert result is not None, "Navigate returned None for path with invalid chars"
            assert isinstance(result, dict), f"Navigate should return dict, got {type(result)}"
            assert "status" in result, "Missing status field"
        except RuntimeError as e:
            # Verify proper error handling
            error_str = str(e)
            assert "RPC error" in error_str, f"Expected RPC error format, got: {error_str}"
            assert any(x in error_str.lower() for x in ["invalid", "character", "path"]), \
                f"Error should indicate invalid characters, got: {error_str}"
        
        # IMPORTANT: Reset to a valid path to ensure shell is in good state for next tests
        import os
        pictures_path = os.path.expanduser("~\\Pictures")
        result = await self.client.call("navigate", {"path": pictures_path})
        assert result["status"] == "ok", "Failed to reset to valid Pictures path"
        print(f"  [DEBUG] Reset shell to valid path: {pictures_path}")
    
    async def test_metadata(self):
        """Test metadata retrieval."""
        paths = ["C:\\Windows", "C:\\Users", "C:\\Program Files"]
        result = await self.client.call("getMetadata", {"paths": paths})
        
        assert "items" in result, "Missing items in metadata"
        assert len(result["items"]) > 0, "No metadata returned"
        
        for item in result["items"]:
            assert "Path" in item, "Missing Path in metadata"
            assert "Exists" in item, "Missing Exists in metadata"
    
    async def test_list_shells(self):
        """Test shell enumeration."""
        result = await self.client.call("listShells", {})
        
        assert "shells" in result, "Missing shells in response"
        assert isinstance(result["shells"], list), "Shells should be a list"
        assert len(result["shells"]) > 0, "Should have at least one shell"
        
        # Check first shell has required fields
        shell = result["shells"][0]
        assert "shellId" in shell, "Missing shellId"
        assert "windowId" in shell, "Missing windowId"
        assert "tabId" in shell, "Missing tabId"
        assert "isActive" in shell, "Missing isActive flag"
        assert "window" in shell, "Missing window info"
        assert "currentPath" in shell, "Missing currentPath"
        assert "availableActions" in shell, "Missing availableActions"
        
        # Verify window info structure
        window = shell["window"]
        assert "pid" in window, "Missing PID in window info"
        assert "title" in window, "Missing title in window info"
        assert "isFocused" in window, "Missing isFocused in window info"
        assert "bounds" in window, "Missing bounds in window info"
        
        print(f"  [INFO] Found {len(result['shells'])} shell(s):")
        for s in result["shells"]:
            print(f"    - Shell {s['shellId'][:8]}... in window {s['windowId']}, path: {s['currentPath']}")
    
    async def test_actions(self):
        """Test ALL available actions dynamically."""
        # Navigate to valid directory first
        await self.client.call("navigate", {"path": "C:\\Users"})
        
        # Wait for navigation to complete and UI to fully initialize
        for i in range(10):  # Try up to 10 times
            await asyncio.sleep(0.5)
            state = await self.client.call("getState", {})
            if not state.get("isLoading", True) and state.get("itemCount", 0) > 0:
                print(f"[DEBUG] Navigation complete after {(i+1)*0.5}s: {state}")
                break
        else:
            print(f"[WARNING] Navigation may not be complete: {state}")
        
        # Debug: Check what shells are available and their state
        shells = await self.client.call("listShells", {})
        print(f"\n[DEBUG] Available shells BEFORE actions: {json.dumps(shells, indent=2)}")
        
        state = await self.client.call("getState", {})
        print(f"\n[DEBUG] Current state: {json.dumps(state, indent=2)}")
        
        # Get ALL available actions from the API
        actions_response = await self.client.call("listActions")
        available_actions = actions_response.get('actions', [])
        
        print(f"\n[INFO] Found {len(available_actions)} available actions")
        
        failed_actions = []
        succeeded_actions = []
        
        # Test EVERY SINGLE ACTION that the API says is available
        for action in available_actions:
            action_id = action['id']
                
            try:
                # Special handling for toggledualpane - check shells after
                if action_id == 'toggledualpane':
                    print(f"  [TESTING] {action_id}: Testing dual pane toggle...")
                    result = await self.client.call("executeAction", {"actionId": action_id})
                    print(f"  [OK] {action_id}: {result}")
                    
                    # Give UI time to create new pane
                    await asyncio.sleep(1.0)
                    
                    # Check shells after toggle
                    shells_after = await self.client.call("listShells", {})
                    print(f"  [DEBUG] Shells AFTER toggledualpane: {len(shells_after['shells'])} shell(s)")
                    for s in shells_after['shells']:
                        print(f"    - Shell {s['shellId'][:8]}... active={s['isActive']}, path={s['currentPath']}")
                else:
                    result = await self.client.call("executeAction", {"actionId": action_id})
                    print(f"  [OK] {action_id}: {result}")
                    
                succeeded_actions.append(action_id)
            except RuntimeError as e:
                print(f"  [FAILED] {action_id}: {e}")
                failed_actions.append((action_id, str(e)))
        
        # Report results
        print(f"\n[SUMMARY] {len(succeeded_actions)}/{len(available_actions)} actions succeeded")
        if failed_actions:
            print(f"[FAILED ACTIONS]: {[f[0] for f in failed_actions]}")
        
        # ALL actions that are advertised as available MUST work
        assert len(failed_actions) == 0, f"{len(failed_actions)} actions failed out of {len(available_actions)}: {[f[0] for f in failed_actions]}"
    
    async def test_invalid_action(self):
        """Test that invalid actions fail properly."""
        try:
            await self.client.call("executeAction", {"actionId": "nonExistentAction"})
            assert False, "Should have failed with invalid action"
        except RuntimeError:
            pass  # Expected to fail
    
    async def test_error_handling(self):
        """Test error handling."""
        # Missing required parameter
        try:
            await self.client.call("navigate", {})
            assert False, "Should have failed with missing parameter"
        except RuntimeError:
            pass  # Expected
        
        # Invalid method
        try:
            await self.client.call("invalidMethod", {})
            assert False, "Should have failed with invalid method"
        except RuntimeError:
            pass  # Expected
    
    async def test_large_payload(self):
        """Test handling of large payloads."""
        # Request metadata for many paths
        paths = [f"C:\\TestPath{i}" for i in range(100)]
        result = await self.client.call("getMetadata", {"paths": paths})
        assert result is not None, "Large payload failed"


async def test_transport(transport: str, token: str, port: int, pipe_name: str) -> bool:
    """Test a specific transport."""
    if transport == "ws":
        client = WebSocketClient(port)
    elif transport == "pipe":
        client = NamedPipeClient(pipe_name)
    else:
        raise ValueError(f"Unknown transport: {transport}")
    
    try:
        await client.connect(token)
        tester = UnifiedIpcTester(client, transport.upper())
        success = await tester.run_all_tests()
        return success
    finally:
        await client.close()


async def test_multi_client(transport: str, token: str, port: int, pipe_name: str):
    """Test multiple simultaneous clients."""
    print(f"\n{'='*60}")
    print(f"Multi-Client Test ({transport.upper()})")
    print(f"{'='*60}")
    
    clients = []
    try:
        # Create 3 clients
        for i in range(3):
            if transport == "ws":
                client = WebSocketClient(port)
            else:
                client = NamedPipeClient(pipe_name)
            
            await client.connect(token)
            clients.append(client)
            print(f"[OK] Client {i+1} connected")
        
        # Test concurrent operations
        tasks = [client.call("getState") for client in clients]
        results = await asyncio.gather(*tasks)
        
        for i, result in enumerate(results):
            assert "currentPath" in result, f"Client {i+1} failed"
            print(f"[OK] Client {i+1} got state: {result['currentPath']}")
        
        print(f"[OK] Multi-client test passed")
        return True
        
    except Exception as e:
        print(f"[FAIL] Multi-client test failed: {e}")
        return False
    
    finally:
        for client in clients:
            await client.close()


async def main():
    parser = argparse.ArgumentParser(description="Unified IPC test suite")
    parser.add_argument("--transport", choices=["ws", "pipe", "both"], default="both",
                       help="Which transport to test")
    parser.add_argument("--test", choices=["basic", "multi", "all"], default="all",
                       help="Which tests to run")
    args = parser.parse_args()
    
    # Discover configuration
    try:
        token, port, pipe_name = discover_ipc_config()
    except Exception as e:
        print(f"Discovery failed: {e}")
        return 1
    
    # Determine which transports to test
    if args.transport == "both":
        transports = ["ws", "pipe"]
    else:
        transports = [args.transport]
    
    # Run tests
    all_passed = True
    
    if args.test in ["basic", "all"]:
        for transport in transports:
            success = await test_transport(transport, token, port, pipe_name)
            all_passed = all_passed and success
    
    if args.test in ["multi", "all"]:
        for transport in transports:
            success = await test_multi_client(transport, token, port, pipe_name)
            all_passed = all_passed and success
    
    # Final summary
    print(f"\n{'='*60}")
    print("FINAL SUMMARY")
    print(f"{'='*60}")
    
    if all_passed:
        print("[OK] ALL TESTS PASSED - Both transports have 1:1 parity!")
        return 0
    else:
        print("[FAIL] SOME TESTS FAILED - Transports do not have full parity")
        return 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))