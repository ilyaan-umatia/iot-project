# A plain Modbus TCP device built on pymodbus (third-party), one container per simulated fridge.
FROM python:3.12-slim
RUN pip install --no-cache-dir pymodbus==3.6.9
COPY device.py /device.py
EXPOSE 502
CMD ["python", "-u", "/device.py"]
