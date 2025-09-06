#!/usr/bin/env python3
"""
Basic Python test client for Files App IPC (WebSocket JSON-RPC).

Usage:
  python scripts/ipc_test.py --token <TOKEN> [--navigate <PATH>] [--duration <SECONDS>]

Notes:
- Ensure Remote control is enabled in Files (Settings > Advanced > Remote control) so the IPC service is running.
- Copy the token from the same settings page and pass it with --token.
- Requires: pip install websockets
"""

import argparse
import asyncio
import json
import os
import signal
import sys
from typing import Any, Dict, Optional

import websockets

WS_URL = "ws://127.0.0.1:52345/"


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


async def run(token: str, navigate_path: Optional[str], duration: int):
    print(f"Connecting to {WS_URL} ...")
    async with websockets.connect(WS_URL, max_size=8 * 1024 * 1024) as ws:
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
        sample_paths = list(dict.fromkeys(sample_paths))  # dedupe, keep order

        print("Calling getMetadata ...")
        metadata = await client.call("getMetadata", {"paths": sample_paths})
        print(f"Metadata: {json.dumps(metadata, indent=2)}")

        if navigate_path:
            print(f"Calling navigate to: {navigate_path}")
            try:
                nav = await client.call("navigate", {"path": navigate_path})
                print(f"Navigate result: {json.dumps(nav, indent=2)}")
            except Exception as ex:
                print(f"Navigate failed: {ex}")

        print(f"Listening for notifications for {duration} seconds (press Ctrl+C to exit early)...")
        try:
            await asyncio.sleep(duration)
        except asyncio.CancelledError:
            pass
        finally:
            await client.stop()


def main():
    parser = argparse.ArgumentParser(description="Files App IPC WebSocket test client")
    parser.add_argument("--token", required=True, help="Authentication token from Files settings")
    parser.add_argument("--navigate", help="Optional path to navigate to (e.g., C:/)")
    parser.add_argument("--duration", type=int, default=10, help="Time in seconds to listen for notifications")
    args = parser.parse_args()

    # Use asyncio.run() for better Windows compatibility
    try:
        asyncio.run(run(args.token, args.navigate, args.duration))
    except KeyboardInterrupt:
        print("\nInterrupted by user")
        return 0


if __name__ == "__main__":
    sys.exit(main())
