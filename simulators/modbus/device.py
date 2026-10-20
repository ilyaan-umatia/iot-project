"""Generic Modbus TCP device using pymodbus.

Holds 100 coils, discrete inputs, holding and input registers, all starting at zero and all
addressed from 0. Any Modbus client can read or write them; the demo console writes the
fridge's physical values here, and the gateway's data acquisition polls them.
"""
import asyncio
import logging
import os

from pymodbus.datastore import ModbusSequentialDataBlock, ModbusServerContext, ModbusSlaveContext
from pymodbus.server import StartAsyncTcpServer

SIZE = 100


async def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
    name = os.environ.get("DEVICE_NAME", "modbus-device")
    port = int(os.environ.get("MODBUS_PORT", "502"))
    device = ModbusSlaveContext(
        di=ModbusSequentialDataBlock(0, [0] * SIZE),
        co=ModbusSequentialDataBlock(0, [0] * SIZE),
        hr=ModbusSequentialDataBlock(0, [0] * SIZE),
        ir=ModbusSequentialDataBlock(0, [0] * SIZE),
        zero_mode=True,
    )
    logging.info("%s: Modbus TCP server on port %s", name, port)
    # single=True: the device answers on any unit ID, like most single-device simulators.
    await StartAsyncTcpServer(context=ModbusServerContext(slaves=device, single=True), address=("0.0.0.0", port))


if __name__ == "__main__":
    asyncio.run(main())
