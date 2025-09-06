#!/usr/bin/env python3
"""Test IPC edge cases and error handling"""
import asyncio
import json
import websockets

WS_URL = "ws://127.0.0.1:52345/"
TOKEN = "41b7bc1c8abb4d1b98bb7466bc8ea96c"

async def test():
    async with websockets.connect(WS_URL) as ws:
        # Handshake
        await ws.send(json.dumps({
            "jsonrpc": "2.0",
            "id": 1,
            "method": "handshake",
            "params": {"token": TOKEN, "clientInfo": "edge-test"}
        }))
        resp = json.loads(await ws.recv())
        print(f"Handshake: {resp.get('result')}\n")
        
        # Test 1: Invalid path navigation
        print("=== Test 1: Navigate to invalid path ===")
        await ws.send(json.dumps({
            "jsonrpc": "2.0",
            "id": 2,
            "method": "navigate",
            "params": {"path": "Z:\\NonExistent\\Path\\That\\Does\\Not\\Exist"}
        }))
        resp = json.loads(await ws.recv())
        print(f"Result: {resp.get('result') or resp.get('error')}")
        
        # Test 2: Invalid action
        print("\n=== Test 2: Execute invalid action ===")
        await ws.send(json.dumps({
            "jsonrpc": "2.0",
            "id": 3,
            "method": "executeAction",
            "params": {"actionId": "thisActionDoesNotExist"}
        }))
        resp = json.loads(await ws.recv())
        print(f"Result: {resp.get('result') or resp.get('error')}")
        
        # Test 3: Missing parameters
        print("\n=== Test 3: Navigate without path ===")
        await ws.send(json.dumps({
            "jsonrpc": "2.0",
            "id": 4,
            "method": "navigate",
            "params": {}
        }))
        resp = json.loads(await ws.recv())
        print(f"Result: {resp.get('result') or resp.get('error')}")
        
        # Test 4: Invalid method
        print("\n=== Test 4: Call non-existent method ===")
        await ws.send(json.dumps({
            "jsonrpc": "2.0",
            "id": 5,
            "method": "thisMethodDoesNotExist",
            "params": {}
        }))
        resp = json.loads(await ws.recv())
        print(f"Result: {resp.get('result') or resp.get('error')}")
        
        # Test 5: Malformed path
        print("\n=== Test 5: Navigate with null characters ===")
        await ws.send(json.dumps({
            "jsonrpc": "2.0",
            "id": 6,
            "method": "navigate",
            "params": {"path": "C:\\Test\x00\\Path"}
        }))
        resp = json.loads(await ws.recv())
        print(f"Result: {resp.get('result') or resp.get('error')}")
        
        # Test 6: Very long path
        print("\n=== Test 6: Navigate with extremely long path ===")
        long_path = "C:\\" + "\\VeryLongFolderName" * 100
        await ws.send(json.dumps({
            "jsonrpc": "2.0",
            "id": 7,
            "method": "navigate",
            "params": {"path": long_path}
        }))
        resp = json.loads(await ws.recv())
        print(f"Result: {resp.get('result') or resp.get('error')}")
        
        # Test 7: Notification (no response expected)
        print("\n=== Test 7: Send notification (no ID) ===")
        await ws.send(json.dumps({
            "jsonrpc": "2.0",
            "method": "getState",
            "params": {}
        }))
        print("Notification sent (no response expected)")
        
        # Wait briefly to see if any notifications come through
        await asyncio.sleep(1)
        
        # Test 8: Get final state to ensure system still works
        print("\n=== Test 8: Verify system still responsive ===")
        await ws.send(json.dumps({
            "jsonrpc": "2.0",
            "id": 8,
            "method": "getState"
        }))
        resp = json.loads(await ws.recv())
        state = resp.get('result', {})
        print(f"Current path: {state.get('currentPath')}")
        print(f"System is {'responsive' if state else 'not responsive'}")

asyncio.run(test())