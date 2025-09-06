#!/usr/bin/env python3
"""Test that multiple clients can connect simultaneously with the same token."""

import asyncio
import json
import os
from pathlib import Path
import websockets


def discover_ipc_config():
    """Discover IPC configuration from rendezvous file."""
    rendezvous_path = Path(os.environ['LOCALAPPDATA']) / 'FilesIPC' / 'ipc.info'
    
    with open(rendezvous_path, 'r') as f:
        data = json.load(f)
    
    return data.get('token'), data.get('webSocketPort', 52345)


async def client_task(client_id: int, token: str, port: int):
    """Individual client connection task."""
    ws_url = f"ws://127.0.0.1:{port}/"
    
    try:
        print(f"[Client {client_id}] Connecting...")
        async with websockets.connect(ws_url) as ws:
            # Handshake
            await ws.send(json.dumps({
                "jsonrpc": "2.0",
                "id": 1,
                "method": "handshake",
                "params": {"token": token, "clientInfo": f"test-client-{client_id}"}
            }))
            
            response = json.loads(await ws.recv())
            
            if 'error' in response and response['error'] is not None:
                print(f"[Client {client_id}] FAILED - Handshake error: {response['error']}")
                return False
            
            print(f"[Client {client_id}] SUCCESS - Authenticated")
            
            # Make a test call
            await ws.send(json.dumps({
                "jsonrpc": "2.0",
                "id": 2,
                "method": "getState"
            }))
            
            response = json.loads(await ws.recv())
            if 'result' in response:
                print(f"[Client {client_id}] SUCCESS - Got state: path={response['result'].get('currentPath')}")
            
            # Keep connection alive briefly
            await asyncio.sleep(2)
            
            print(f"[Client {client_id}] Disconnecting...")
            return True
            
    except Exception as e:
        print(f"[Client {client_id}] ERROR: {e}")
        return False


async def main():
    print("Testing multi-client support...")
    print("=" * 50)
    
    # Discover config
    try:
        token, port = discover_ipc_config()
        print(f"Discovered: token={token[:8]}..., port={port}")
    except Exception as e:
        print(f"Discovery failed: {e}")
        return
    
    print("\nStarting 3 simultaneous client connections...")
    print("-" * 50)
    
    # Launch multiple clients simultaneously
    tasks = [
        client_task(i, token, port)
        for i in range(1, 4)
    ]
    
    results = await asyncio.gather(*tasks)
    
    print("\n" + "=" * 50)
    success_count = sum(1 for r in results if r)
    print(f"Result: {success_count}/3 clients connected successfully")
    
    if success_count == 3:
        print("SUCCESS: Multi-client support VERIFIED!")
    else:
        print("FAILED: Multi-client support NOT working!")


if __name__ == "__main__":
    asyncio.run(main())