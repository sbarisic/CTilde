#!/usr/bin/env python3
"""Exercise an existing ManagedShell SSH server; requires Paramiko 5 and pyserial.

Uses an unlocked SSH agent and verified known-hosts file. Does not flash, change
credentials, or write device files. COM port access does not assert DTR or RTS.
"""
import argparse
import json
from pathlib import Path
import socket
import time

import paramiko
from paramiko.kex_curve25519 import KexCurve25519
import serial


class RfcTransport(paramiko.Transport):
    # Paramiko 5 implements this exchange under its older libssh.org alias.
    _kex_info = {**paramiko.Transport._kex_info, 'curve25519-sha256': KexCurve25519}

    def _sanitize_window_size(self, size):
        # The fixture deliberately tests windows below Paramiko's 32 KiB floor.
        if size is not None and 256 <= size < 32768:
            return size
        return super()._sanitize_window_size(size)


def connect(args):
    agent = paramiko.Agent()
    transport = None
    try:
        keys = [key for key in agent.get_keys() if key.fingerprint == args.fingerprint]
        if len(keys) != 1:
            raise RuntimeError('Load the selected key into the local SSH agent first.')
        transport = RfcTransport(socket.create_connection((args.host, args.ssh_port), timeout=10))
        options = transport.get_security_options()
        options.kex = ('curve25519-sha256',)
        options.ciphers = ('aes128-gcm@openssh.com',)
        options.key_types = ('ecdsa-sha2-nistp256',)
        transport.start_client(timeout=15)
        hosts = paramiko.HostKeys(str(args.known_hosts))
        host_name = args.host if args.ssh_port == 22 else f'[{args.host}]:{args.ssh_port}'
        if not hosts.check(host_name, transport.get_remote_server_key()):
            raise RuntimeError('Server host key does not match the verified known-hosts file.')
        transport.auth_publickey(args.user, keys[0])
        return transport
    except BaseException:
        if transport is not None:
            transport.close()
        raise
    finally:
        agent.close()


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--host', required=True)
parser.add_argument('--ssh-port', type=int, default=22)
parser.add_argument('--user', default='ctilde')
parser.add_argument('--known-hosts', type=Path, required=True)
parser.add_argument('--fingerprint', required=True, help='SHA256 fingerprint of an unlocked agent key')
parser.add_argument('--port', required=True, help='UART port; close the serial monitor first')
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--overflow-pressure', action='store_true',
                    help='Require controlled disconnect under a 32,768-byte payload while a shell is active')
args = parser.parse_args()
out = args.output
out.mkdir(parents=True, exist_ok=True)
report = {'schemaVersion': 1, 'passed': False, 'checks': {},
          'limitations': ['Prompt Ctrl+C is not foreground-child cancellation acceptance.',
                         'No maximum-packet, separate-stderr, throughput, or stack acceptance.']}
t = None

def receive(c, predicate, timeout=30):
    data = bytearray()
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if c.recv_ready():
            data.extend(c.recv(32768))
        if predicate(bytes(data)):
            return bytes(data)
        if c.closed and (not c.recv_ready()):
            break
        time.sleep(0.01)
    raise AssertionError('Output condition failed: ' + repr(bytes(data)[-300:]))

def finish(c):
    data = receive(c, lambda _: c.exit_status_ready() and (not c.recv_ready()), 30)
    assert c.exit_status_ready()
    return (c.recv_exit_status(), data)
try:
    t = connect(args)
    c = t.open_session(timeout=10)
    c.settimeout(20)
    c.get_pty(width=80, height=24)
    c.invoke_shell()
    receive(c, lambda d: b'ct> ' in d)
    c.sendall(b'fr')
    c.resize_pty(width=100, height=30)
    c.sendall(b'ee\r')
    data = receive(c, lambda d: b'SPIRAM' in d)
    assert b'free heap:' in data and b'unknown command' not in data
    report['checks']['resizeDuringInput'] = True
    for part in [b'\x1b', b'[', b'8;40;100', b't', b'free\r']:
        c.sendall(part)
        time.sleep(0.15)
    data = receive(c, lambda d: b'SPIRAM' in d)
    assert b'free heap:' in data and b'unknown command' not in data
    report['checks']['fragmentedCsi'] = True
    c.sendall(b'garbage\x03free\r')
    data = receive(c, lambda d: b'SPIRAM' in d)
    assert b'^C' in data and b'free heap:' in data and (b'unknown command' not in data)
    report['checks']['promptCtrlC'] = True
    t.renegotiate_keys()
    c.sendall(b'free\r')
    data = receive(c, lambda d: b'SPIRAM' in d)
    assert b'free heap:' in data
    report['checks']['rekey'] = True
    c.shutdown_write()
    status, _ = finish(c)
    assert status == 0
    t.close()
    t = None
    t = connect(args)
    c = t.open_session(window_size=1024, timeout=10)
    c.settimeout(20)
    c.get_pty(width=80, height=24)
    c.invoke_shell()
    receive(c, lambda d: b'ct> ' in d)
    c.sendall(b'free\r' * 10 + b'\x04')
    time.sleep(2)
    port = serial.Serial(port=None, baudrate=115200, timeout=0.1, write_timeout=2)
    port.dtr = False
    port.rts = False
    port.port = args.port
    port.open()
    try:
        port.write(b'ps\r')
        uart = ''
        deadline = time.monotonic() + 2
        while time.monotonic() < deadline:
            uart += port.read(4096).decode('utf-8', 'replace')
    finally:
        port.close()
    (out / 'stalled-uart.txt').write_text(uart)
    assert 'processes:' in uart
    report['checks']['uartWhileClientStalled'] = True
    status, data = finish(c)
    (out / 'stalled-output.bin').write_bytes(data)
    assert status == 0 and data.count(b'free heap:') == 10 and (b'unknown command' not in data)
    report['checks']['stalledClientAnd1024Window'] = True
    t.close()
    t = None
    t = connect(args)
    c = t.open_session(timeout=10)
    c.settimeout(20)
    c.exec_command('no-such-command')
    c.shutdown_write()
    status, data = finish(c)
    assert status == 127 and b'unknown command' in data
    report['checks']['nonzeroExitStatus'] = True
    t.close()
    t = None
    if args.overflow_pressure:
        t = connect(args)
        c = t.open_session(timeout=10)
        c.settimeout(20)
        c.get_pty(width=80, height=24)
        c.invoke_shell()
        receive(c, lambda data: b'ct> ' in data)
        # SSH_MSG_IGNORE: one message byte, four length bytes, 32,763 data bytes.
        # This is a legal maximum payload, unrelated to the channel window.
        message = paramiko.Message()
        message.add_byte(b'\x02')
        message.add_string(b'x' * 32763)
        t._send_user_message(message)
        deadline = time.monotonic() + 20
        while t.is_active() and time.monotonic() < deadline:
            time.sleep(0.05)
        if t.is_active():
            raise AssertionError('Pressure fixture did not disconnect; confirm allocation pressure before interpreting this gate.')
        t.close()
        t = None
        t = connect(args)
        c = t.open_session(timeout=10)
        c.settimeout(20)
        c.exec_command('free')
        c.shutdown_write()
        status, data = finish(c)
        assert status == 0 and b'free heap:' in data
        report['checks']['overflowDisconnectAndSubsequentLogin'] = True
        report['overflowPayloadBytes'] = 32768
        report['overflowLimitation'] = 'Correlate with the production allocation-failure log; this does not prove maximum-packet success when memory is available.'
    report['passed'] = True
except Exception as e:
    report['error'] = repr(e)
    raise
finally:
    if t is not None:
        t.close()
    (out / 'report.json').write_text(json.dumps(report, indent=2))
    print(report, flush=True)
