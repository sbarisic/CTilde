#!/usr/bin/env python3
"""Check an existing ManagedShell SSH server with locally unlocked OpenSSH credentials."""
import argparse
import ctypes
import hashlib
import json
import os
import signal
from pathlib import Path
import subprocess
import time
import uuid


def run_owned(command, timeout):
    started = time.monotonic()
    process = subprocess.Popen(command, stdin=subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               start_new_session=os.name != 'nt')
    job = None
    kernel = None
    if os.name == 'nt':
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p]
        kernel.CreateJobObjectW.restype = ctypes.c_void_p
        kernel.AssignProcessToJobObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        kernel.TerminateJobObject.argtypes = [ctypes.c_void_p, ctypes.c_uint]
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        job = kernel.CreateJobObjectW(None, None)
        if not job or not kernel.AssignProcessToJobObject(job, int(process._handle)):
            process.kill()
            process.communicate()
            if job:
                kernel.CloseHandle(job)
            raise ctypes.WinError(ctypes.get_last_error())
    timed_out = False
    try:
        stdout, stderr = process.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        timed_out = True
        if job:
            kernel.TerminateJobObject(job, 1)
        else:
            os.killpg(process.pid, signal.SIGKILL)
        stdout, stderr = process.communicate()
    finally:
        if job:
            kernel.TerminateJobObject(job, 1)
            kernel.CloseHandle(job)
    return dict(exitCode=process.returncode, timedOut=timed_out,
                elapsedSeconds=round(time.monotonic() - started, 3),
                stdout=stdout.decode('utf-8', errors='replace'),
                stderr=stderr.decode('utf-8', errors='replace'))


def batch_quote(path):
    text = str(path).replace('\\', '/')
    if any(character in text for character in '\r\n"'):
        raise ValueError('SFTP paths cannot contain line breaks or double quotes')
    return '"' + text + '"'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', required=True)
    parser.add_argument('--user', default='ctilde')
    parser.add_argument('--port', type=int, default=22)
    parser.add_argument('--identity', required=True, type=Path)
    parser.add_argument('--known-hosts', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--bytes', type=int, default=4096)
    parser.add_argument('--chunk', type=int, default=1024)
    parser.add_argument('--timeout', type=int, default=60)
    args = parser.parse_args()
    if args.bytes < 1 or not 1 <= args.chunk <= 16384 or args.timeout < 1:
        parser.error('Use positive file size and timeout, and a chunk between 1 and 16384 bytes')
    if args.host.startswith('-') or args.user.startswith('-'):
        parser.error('Host and user cannot begin with a hyphen')
    if not args.identity.is_file() or not args.known_hosts.is_file():
        parser.error('The existing identity and verified known-hosts files are required')
    args.output.mkdir(parents=True, exist_ok=True)
    common = ['-o', 'BatchMode=yes', '-o', 'IdentitiesOnly=yes',
              '-o', 'StrictHostKeyChecking=yes', '-o', 'ConnectTimeout=10',
              '-o', 'ServerAliveInterval=5', '-o', 'ServerAliveCountMax=3',
              '-o', 'UserKnownHostsFile=' + str(args.known_hosts.resolve()),
              '-i', str(args.identity.resolve())]
    target = args.user + '@' + args.host
    report = dict(schemaVersion=1, passed=False, host=args.host, fileBytes=args.bytes,
                  chunkBytes=args.chunk,
                  limitations=['No interactive, maximum-packet, rekey, or stack acceptance.'])
    command = run_owned(['ssh', '-v', '-T', '-p', str(args.port), *common, target, 'free'], args.timeout)
    report['command'] = command
    report['authenticated'] = 'Authenticated to ' in command['stderr']
    report['commandPassed'] = command['exitCode'] == 0 and 'free heap:' in command['stdout']
    upload = args.output.resolve() / 'upload.bin'
    download = args.output.resolve() / ('download-' + uuid.uuid4().hex + '.bin')
    upload.write_bytes(bytes((index * 31 + 7) % 256 for index in range(args.bytes)))
    remote = '/draft051-' + uuid.uuid4().hex + '.bin'
    batch = args.output.resolve() / 'transfer.batch'
    batch.write_text('\n'.join([
        'put ' + batch_quote(upload) + ' ' + batch_quote(remote),
        'get ' + batch_quote(remote) + ' ' + batch_quote(download),
        'rm ' + batch_quote(remote), 'bye', '']), encoding='utf-8')
    sftp_command = ['sftp', '-P', str(args.port), *common, '-B', str(args.chunk), '-R', '1']
    transfer = run_owned([*sftp_command, '-b', str(batch), target], args.timeout)
    report['transfer'] = transfer
    report['uploadSha256'] = hashlib.sha256(upload.read_bytes()).hexdigest()
    report['downloadSha256'] = hashlib.sha256(download.read_bytes()).hexdigest() if download.exists() else None
    report['transferPassed'] = transfer['exitCode'] == 0 and report['uploadSha256'] == report['downloadSha256']
    if transfer['exitCode'] != 0:
        cleanup = args.output.resolve() / 'cleanup.batch'
        cleanup.write_text('-rm ' + batch_quote(remote) + '\nbye\n', encoding='utf-8')
        report['cleanup'] = run_owned([*sftp_command, '-b', str(cleanup), target], args.timeout)
        report['possiblyRemainingRemoteFile'] = remote if report['cleanup']['exitCode'] != 0 else None
    report['passed'] = report['authenticated'] and report['commandPassed'] and report['transferPassed']
    (args.output / 'ssh-acceptance.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print('Public-key authentication:', report['authenticated'])
    print('Remote command:', report['commandPassed'])
    print('SFTP hash comparison:', report['transferPassed'])
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
