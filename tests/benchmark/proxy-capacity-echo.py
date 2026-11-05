#!/usr/bin/env python3
"""Small asyncio TCP echo target for isolated SOCKS5/CONNECT capacity runs."""

import argparse
import asyncio
import os
from pathlib import Path


async def echo(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
    try:
        while data := await reader.read(65536):
            writer.write(data)
            await writer.drain()
    finally:
        writer.close()
        await writer.wait_closed()


async def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bind", required=True)
    parser.add_argument("--port", required=True, type=int)
    parser.add_argument("--pid-file", required=True)
    args = parser.parse_args()
    server = await asyncio.start_server(echo, args.bind, args.port)
    Path(args.pid_file).write_text(str(os.getpid()), encoding="ascii")
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
