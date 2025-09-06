#!/usr/bin/env python3
import sys
import asyncio
import websockets
import json

print("Starting test...", flush=True)
sys.stdout.flush()

async def test():
    print(f"Attempting to connect to ws://127.0.0.1:52345/", flush=True)
    sys.stdout.flush()
    
    try:
        async with websockets.connect("ws://127.0.0.1:52345/") as ws:
            print("Connected! Sending handshake...", flush=True)
            sys.stdout.flush()
            
            msg = {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "handshake",
                "params": {"token": "41b7bc1c8abb4d1b98bb7466bc8ea96c", "clientInfo": "test"}
            }
            await ws.send(json.dumps(msg))
            print("Handshake sent, waiting for response...", flush=True)
            sys.stdout.flush()
            
            response = await ws.recv()
            print(f"Response: {response}", flush=True)
            sys.stdout.flush()
            
    except Exception as e:
        print(f"ERROR: {e}", flush=True)
        sys.stdout.flush()

print("Running async test...", flush=True)
sys.stdout.flush()
asyncio.run(test())
print("Done!", flush=True)