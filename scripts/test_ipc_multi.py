#!/usr/bin/env python3
"""Test IPC with multiple operations"""
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
            "params": {"token": TOKEN, "clientInfo": "multi-test"}
        }))
        resp = json.loads(await ws.recv())
        print(f"Handshake: {resp.get('result')}")
        
        # Test executeAction - refresh
        print("\n=== Testing executeAction: refresh ===")
        await ws.send(json.dumps({
            "jsonrpc": "2.0",
            "id": 2,
            "method": "executeAction",
            "params": {"actionId": "refresh"}
        }))
        resp = json.loads(await ws.recv())
        print(f"Refresh result: {resp.get('result') or resp.get('error')}")
        
        # Navigate to different locations quickly
        print("\n=== Testing rapid navigation ===")
        paths = [
            "C:\\Windows",
            "C:\\Program Files",
            "C:\\Users",
            "C:\\Users\\will\\Documents"
        ]
        
        for i, path in enumerate(paths, start=3):
            print(f"Navigating to: {path}")
            await ws.send(json.dumps({
                "jsonrpc": "2.0",
                "id": i,
                "method": "navigate",
                "params": {"path": path}
            }))
            
            # Keep reading until we get our response (skip notifications)
            while True:
                resp = json.loads(await ws.recv())
                if resp.get('id') == i:
                    result = resp.get('result')
                    if result:
                        print(f"  Result: {result.get('status')}")
                    else:
                        print(f"  Error: {resp.get('error')}")
                    break
                else:
                    # It's a notification
                    if resp.get('method'):
                        print(f"  [Notification: {resp['method']}]")
            
            await asyncio.sleep(0.5)
        
        # Get final state
        print("\n=== Final state ===")
        await ws.send(json.dumps({
            "jsonrpc": "2.0",
            "id": 10,
            "method": "getState"
        }))
        resp = json.loads(await ws.recv())
        state = resp.get('result', {})
        print(f"Current path: {state.get('currentPath')}")
        print(f"Can go back: {state.get('canNavigateBack')}")
        print(f"Can go forward: {state.get('canNavigateForward')}")
        print(f"Item count: {state.get('itemCount')}")
        
        # Test getMetadata with multiple paths
        print("\n=== Testing getMetadata with multiple paths ===")
        await ws.send(json.dumps({
            "jsonrpc": "2.0",
            "id": 11,
            "method": "getMetadata",
            "params": {
                "paths": [
                    "C:\\Windows\\System32",
                    "C:\\Windows\\explorer.exe",
                    "C:\\NonExistent\\Path",
                    "C:\\Users\\will\\.gitconfig"
                ]
            }
        }))
        resp = json.loads(await ws.recv())
        items = resp.get('result', {}).get('items', [])
        for item in items:
            print(f"  {item['Path']}: Exists={item['Exists']}, IsDir={item.get('IsDirectory')}, Size={item.get('SizeBytes')}")

asyncio.run(test())