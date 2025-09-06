#!/usr/bin/env python3
"""
Comprehensive IPC test suite for Files App (WebSocket JSON-RPC).

Usage:
  python scripts/ipc_test.py [--test <TEST_NAME>] [--navigate <PATH>] [--duration <SECONDS>]

Tests available:
  - basic: Basic connection and handshake
  - multi: Multiple operations and rapid navigation
  - edge: Edge cases and error handling
  - pathfix: Test the invalid path fix (50-second hang bug)
  - all: Run all tests

Notes:
- Ensure Remote control is enabled in Files (Settings > Advanced > Remote control)
- Token and port are automatically discovered from rendezvous file
- Requires: pip install websockets
"""

import argparse
import asyncio
import json
import os
import sys
import time
from pathlib import Path
from typing import Any, Dict, Optional, Tuple

import websockets


def discover_ipc_config() -> Tuple[str, int, Optional[str]]:
    """Discover IPC configuration from rendezvous file.
    
    Returns:
        Tuple of (token, websocket_port, pipe_name)
    """
    rendezvous_path = Path(os.environ['LOCALAPPDATA']) / 'FilesIPC' / 'ipc.info'
    
    if not rendezvous_path.exists():
        raise FileNotFoundError(
            f"Rendezvous file not found at {rendezvous_path}\n"
            "Make sure Files App is running with Remote Control enabled"
        )
    
    try:
        with open(rendezvous_path, 'r') as f:
            data = json.load(f)
        
        token = data.get('token')
        port = data.get('webSocketPort')
        pipe = data.get('pipeName')
        
        if not token:
            raise ValueError("No token found in rendezvous file")
        
        if not port and not pipe:
            raise ValueError("No connection endpoint found (neither WebSocket port nor pipe name)")
        
        print(f"[Discovery] Found IPC config:")
        print(f"  Token: {token[:8]}...")
        if port:
            print(f"  WebSocket Port: {port}")
        if pipe:
            print(f"  Named Pipe: {pipe}")
        print(f"  Server PID: {data.get('serverPid')}")
        print(f"  Created: {data.get('createdUtc')}")
        
        return token, port or 52345, pipe
        
    except json.JSONDecodeError as e:
        raise ValueError(f"Invalid JSON in rendezvous file: {e}")
    except Exception as e:
        raise RuntimeError(f"Failed to read rendezvous file: {e}")


class JsonRpcClient:
    def __init__(self, ws):
        self.ws = ws
        self._next_id = 1
        self._response_futures: dict[int, asyncio.Future] = {}
        self._receiver_task: Optional[asyncio.Task] = None

    async def start_receiver(self):
        self._receiver_task = asyncio.create_task(self._receiver_loop())

    async def stop(self):
        if self._receiver_task and not self._receiver_task.done():
            self._receiver_task.cancel()
            try:
                await self._receiver_task
            except asyncio.CancelledError:
                pass

    async def _receiver_loop(self):
        async for msg in self.ws:
            try:
                data = json.loads(msg)
            except Exception:
                print(f"<< malformed message: {msg}")
                continue

            if isinstance(data, dict) and "method" in data and "id" not in data:
                # Notification
                print(f"<< notification: {data['method']} params={data.get('params')}")
                continue

            # Response
            _id = data.get("id")
            fut = self._response_futures.pop(_id, None)
            if fut is not None and not fut.done():
                fut.set_result(data)
            else:
                print(f"<< unsolicited response: {data}")

    async def call(self, method: str, params: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        _id = self._next_id
        self._next_id += 1
        req = {
            "jsonrpc": "2.0",
            "id": _id,
            "method": method,
        }
        if params is not None:
            req["params"] = params

        fut: asyncio.Future = asyncio.get_event_loop().create_future()
        self._response_futures[_id] = fut

        await self.ws.send(json.dumps(req))
        resp = await fut
        if "error" in resp and resp["error"] is not None:
            raise RuntimeError(f"RPC error for {method}: {resp['error']}")
        return resp.get("result")


async def test_basic(client: JsonRpcClient):
    """Test basic functionality"""
    print("\n=== BASIC TEST ===")
    
    print("Calling getState ...")
    state = await client.call("getState")
    print(f"State: {json.dumps(state, indent=2)}")

    print("Calling listActions ...")
    actions = await client.call("listActions")
    print(f"Actions: {json.dumps(actions, indent=2)}")

    sample_paths = []
    userprofile = os.environ.get("USERPROFILE")
    if userprofile:
        sample_paths.append(userprofile)
    sample_paths.append(os.path.expanduser("~"))
    sample_paths.append("C:/")
    sample_paths = list(dict.fromkeys(sample_paths))  # dedupe

    print("Calling getMetadata ...")
    metadata = await client.call("getMetadata", {"paths": sample_paths})
    print(f"Metadata: {json.dumps(metadata, indent=2)}")


async def test_multi(client: JsonRpcClient):
    """Test multiple operations"""
    print("\n=== MULTI-OPERATION TEST ===")
    
    # Test executeAction - refresh
    print("Testing executeAction: refresh")
    try:
        result = await client.call("executeAction", {"actionId": "refresh"})
        print(f"Refresh result: {result}")
    except Exception as e:
        print(f"Refresh error: {e}")
    
    # Navigate to different locations quickly
    print("\nTesting rapid navigation:")
    paths = [
        "C:\\Windows",
        "C:\\Program Files",
        "C:\\Users",
        os.path.expanduser("~/Documents")
    ]
    
    for path in paths:
        print(f"Navigating to: {path}")
        try:
            result = await client.call("navigate", {"path": path})
            print(f"  Result: {result.get('status')}")
        except Exception as e:
            print(f"  Error: {e}")
        await asyncio.sleep(0.5)
    
    # Get final state
    print("\nFinal state:")
    state = await client.call("getState")
    print(f"Current path: {state.get('currentPath')}")
    print(f"Can go back: {state.get('canNavigateBack')}")
    print(f"Can go forward: {state.get('canNavigateForward')}")
    print(f"Item count: {state.get('itemCount')}")


async def test_edge_cases(client: JsonRpcClient):
    """Test edge cases and error handling"""
    print("\n=== EDGE CASES TEST ===")
    
    # Test 1: Invalid path navigation
    print("Test 1: Navigate to invalid path")
    try:
        result = await client.call("navigate", {"path": "Z:\\NonExistent\\Path\\That\\Does\\Not\\Exist"})
        print(f"Result: {result}")
    except Exception as e:
        print(f"Error (expected): {e}")
    
    # Test 2: Invalid action
    print("\nTest 2: Execute invalid action")
    try:
        result = await client.call("executeAction", {"actionId": "thisActionDoesNotExist"})
        print(f"Result: {result}")
    except Exception as e:
        print(f"Error (expected): {e}")
    
    # Test 3: Missing parameters
    print("\nTest 3: Navigate without path")
    try:
        result = await client.call("navigate", {})
        print(f"Result: {result}")
    except Exception as e:
        print(f"Error (expected): {e}")
    
    # Test 4: Invalid method
    print("\nTest 4: Call non-existent method")
    try:
        result = await client.call("thisMethodDoesNotExist", {})
        print(f"Result: {result}")
    except Exception as e:
        print(f"Error (expected): {e}")
    
    # Test 5: Very long path
    print("\nTest 5: Navigate with extremely long path")
    long_path = "C:\\" + "\\VeryLongFolderName" * 100
    try:
        result = await client.call("navigate", {"path": long_path})
        print(f"Result: {result}")
    except Exception as e:
        print(f"Error (expected): {e}")
    
    # Test 6: Verify system still responsive
    print("\nTest 6: Verify system still responsive")
    state = await client.call("getState")
    print(f"Current path: {state.get('currentPath')}")
    print(f"System is {'responsive' if state else 'not responsive'}")


async def test_pathfix(client: JsonRpcClient):
    """Test the fix for 50-second hang on invalid paths"""
    print("\n=== PATH VALIDATION FIX TEST ===")
    print("This tests the fix for the 50-second hang on invalid paths")
    
    # Test 1: Non-existent normal path
    print("\nTest 1: Non-existent normal path (should fail quickly)")
    start = time.time()
    try:
        result = await client.call("navigate", {"path": "C:\\ThisPathDoesNotExist\\SubFolder"})
        print(f"Result: {result}")
    except Exception as e:
        print(f"Error: {e}")
    elapsed = time.time() - start
    print(f"Time taken: {elapsed:.2f} seconds")
    if elapsed > 5:
        print("WARNING: Took too long! Path validation might not be working.")
    else:
        print("GOOD: Failed quickly as expected")
    
    # Test 2: Extremely long non-existent path (the 50-second hang case)
    print("\nTest 2: Extremely long path (should fail quickly, not hang for 50s)")
    long_path = "C:\\" + "\\VeryLongFolderName" * 200  # Create a path >32k chars
    print(f"Path length: {len(long_path)} characters")
    start = time.time()
    try:
        result = await client.call("navigate", {"path": long_path})
        print(f"Result: {result}")
    except Exception as e:
        print(f"Error: {e}")
    elapsed = time.time() - start
    print(f"Time taken: {elapsed:.2f} seconds")
    if elapsed > 5:
        print("FAILED: Path validation fix not working! Still hanging on invalid paths.")
    else:
        print("SUCCESS: Path validation fix is working! No more 50-second hangs.")
    
    # Test 3: Valid path (should work normally)
    print("\nTest 3: Valid path (should work normally)")
    start = time.time()
    try:
        result = await client.call("navigate", {"path": "C:\\Windows"})
        print(f"Result: {result}")
    except Exception as e:
        print(f"Error: {e}")
    elapsed = time.time() - start
    print(f"Time taken: {elapsed:.2f} seconds")
    
    # Test 4: Network path (might timeout legitimately)
    print("\nTest 4: Network path (might timeout - that's OK for network)")
    start = time.time()
    try:
        result = await client.call("navigate", {"path": "\\\\NonExistentServer\\Share"})
        print(f"Result: {result}")
    except Exception as e:
        print(f"Error: {e}")
    elapsed = time.time() - start
    print(f"Time taken: {elapsed:.2f} seconds")
    if elapsed > 10:
        print("Note: Network paths may timeout - that's expected behavior")


async def run(token: str, port: int, test_name: str, navigate_path: Optional[str], duration: int):
    ws_url = f"ws://127.0.0.1:{port}/"
    print(f"Connecting to {ws_url} ...")
    async with websockets.connect(ws_url, max_size=8 * 1024 * 1024) as ws:
        client = JsonRpcClient(ws)
        await client.start_receiver()

        print("Performing handshake ...")
        try:
            result = await client.call("handshake", {"token": token, "clientInfo": "python-test"})
        except Exception as ex:
            print(f"Handshake failed: {ex}")
            print("- Make sure Remote control is enabled in Files settings")
            print("- Verify the token is correct")
            raise SystemExit(2)

        print(f"Handshake OK: {result}")

        # Run requested tests
        if test_name in ["basic", "all"]:
            await test_basic(client)
        
        if test_name in ["multi", "all"]:
            await test_multi(client)
        
        if test_name in ["edge", "all"]:
            await test_edge_cases(client)
        
        if test_name in ["pathfix", "all"]:
            await test_pathfix(client)
        
        # Optional navigation
        if navigate_path:
            print(f"\nNavigating to: {navigate_path}")
            try:
                nav = await client.call("navigate", {"path": navigate_path})
                print(f"Navigate result: {json.dumps(nav, indent=2)}")
            except Exception as ex:
                print(f"Navigate failed: {ex}")

        # Listen for notifications
        if duration > 0:
            print(f"\nListening for notifications for {duration} seconds (press Ctrl+C to exit)...")
            try:
                await asyncio.sleep(duration)
            except asyncio.CancelledError:
                pass
        
        await client.stop()


def main():
    parser = argparse.ArgumentParser(description="Files App IPC WebSocket test client")
    parser.add_argument("--token", help="Override authentication token (normally auto-discovered)")
    parser.add_argument("--port", type=int, help="Override WebSocket port (normally auto-discovered)")
    parser.add_argument("--test", choices=["basic", "multi", "edge", "pathfix", "all"], 
                       default="basic", help="Which test to run")
    parser.add_argument("--navigate", help="Optional path to navigate to")
    parser.add_argument("--duration", type=int, default=0, 
                       help="Time in seconds to listen for notifications after tests")
    args = parser.parse_args()

    try:
        # Auto-discover or use overrides
        if args.token and args.port:
            token = args.token
            port = args.port
            print(f"Using manual configuration: token={token[:8]}..., port={port}")
        else:
            try:
                token, port, pipe_name = discover_ipc_config()
                # Allow partial overrides
                if args.token:
                    token = args.token
                    print(f"Using override token: {token[:8]}...")
                if args.port:
                    port = args.port
                    print(f"Using override port: {port}")
            except Exception as e:
                print(f"Auto-discovery failed: {e}")
                print("\nYou can specify connection details manually:")
                print("  --token <TOKEN> --port <PORT>")
                return 1
        
        asyncio.run(run(token, port, args.test, args.navigate, args.duration))
    except KeyboardInterrupt:
        print("\nInterrupted by user")
        return 0


if __name__ == "__main__":
    sys.exit(main())