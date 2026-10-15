"""Bounded trusted-fixture subprocesses; cancellation is not a security boundary."""
from __future__ import annotations

import ctypes
from dataclasses import dataclass
import os
from pathlib import Path
import signal
import subprocess
import threading
import time
from typing import Callable


@dataclass
class ProcessResult:
    exit_code: int | None
    elapsed_seconds: float
    stdout: bytes
    stderr: bytes
    failure: str | None
    trace_truncated: bool
    errors: tuple[str, ...]


class _WindowsJob:
    """Assign the suspended child before it can create any descendants."""

    def __init__(self):
        from ctypes import wintypes as w

        class Basic(ctypes.Structure):
            _fields_ = [
                ("PerProcessUserTimeLimit", ctypes.c_longlong),
                ("PerJobUserTimeLimit", ctypes.c_longlong), ("LimitFlags", w.DWORD),
                ("MinimumWorkingSetSize", ctypes.c_size_t), ("MaximumWorkingSetSize", ctypes.c_size_t),
                ("ActiveProcessLimit", w.DWORD), ("Affinity", ctypes.c_size_t),
                ("PriorityClass", w.DWORD), ("SchedulingClass", w.DWORD),
            ]

        class IO(ctypes.Structure):
            _fields_ = [(name, ctypes.c_ulonglong) for name in
                        ("ReadOperationCount", "WriteOperationCount", "OtherOperationCount",
                         "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]

        class Extended(ctypes.Structure):
            _fields_ = [("BasicLimitInformation", Basic), ("IoInfo", IO),
                        ("ProcessMemoryLimit", ctypes.c_size_t), ("JobMemoryLimit", ctypes.c_size_t),
                        ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]

        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, w.LPCWSTR]
        self.kernel.CreateJobObjectW.restype = w.HANDLE
        self.kernel.SetInformationJobObject.argtypes = [w.HANDLE, ctypes.c_int, ctypes.c_void_p, w.DWORD]
        self.kernel.AssignProcessToJobObject.argtypes = [w.HANDLE, w.HANDLE]
        self.kernel.TerminateJobObject.argtypes = [w.HANDLE, w.UINT]
        self.kernel.CloseHandle.argtypes = [w.HANDLE]
        self.handle = self.kernel.CreateJobObjectW(None, None)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())
        limits = Extended()
        limits.BasicLimitInformation.LimitFlags = 0x2000  # KILL_ON_JOB_CLOSE
        if not self.kernel.SetInformationJobObject(self.handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
            self.close()
            raise ctypes.WinError(ctypes.get_last_error())

    def assign_and_resume(self, process: subprocess.Popen):
        if not self.kernel.AssignProcessToJobObject(self.handle, int(process._handle)):
            raise ctypes.WinError(ctypes.get_last_error())
        # Popen closes the initial thread handle. Resume the suspended process
        # only after job assignment; no uncontained launch fallback is allowed.
        resume = ctypes.WinDLL("ntdll").NtResumeProcess
        resume.argtypes = [ctypes.c_void_p]
        resume.restype = ctypes.c_long
        if resume(int(process._handle)) != 0:
            raise OSError("cannot resume job-contained offline fixture")

    def terminate(self):
        if self.handle and not self.kernel.TerminateJobObject(self.handle, 1):
            raise ctypes.WinError(ctypes.get_last_error())

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None


def capture_process(
    argv: list[str], cwd: Path, env: dict[str, str], timeout: float, max_bytes: int,
    on_line: Callable[[bytes, subprocess.Popen], None] | None = None,
) -> ProcessResult:
    """Execute argv without a shell, bounding combined streams and wall time."""
    if Path(argv[0]).suffix.lower() in {".cmd", ".bat"}:
        raise ValueError("shell wrappers are not executed; configure an .exe or interpreter argv")
    start = time.monotonic()
    job = _WindowsJob() if os.name == "nt" else None
    process = None
    chunks: list[bytearray] = [bytearray(), bytearray()]
    overflow = threading.Event()
    callback_errors: list[Exception] = []
    lock = threading.Lock()
    readers = []
    failure = None
    total = 0
    try:
        process = subprocess.Popen(
            argv, cwd=cwd, env=env, shell=False, stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            creationflags=0x00000004 if job else 0,  # CREATE_SUSPENDED
            start_new_session=not bool(job),
        )
        if job:
            try:
                job.assign_and_resume(process)
            except Exception:
                process.kill()  # Also reap a child that was never assigned to the job.
                raise

        def read_stream(index, stream):
            nonlocal total
            pending = b""
            try:
                while block := os.read(stream.fileno(), 4096):
                    with lock:
                        remaining = max_bytes - total
                        chunks[index].extend(block[:remaining])
                        total += min(len(block), remaining)
                        if len(block) > remaining:
                            overflow.set()
                    if on_line and index == 0 and not overflow.is_set():
                        pending += block
                        while b"\n" in pending:
                            line, pending = pending.split(b"\n", 1)
                            on_line(line, process)
                    if overflow.is_set():
                        return
            except Exception as error:
                callback_errors.append(error)
            finally:
                stream.close()

        for index, stream in enumerate((process.stdout, process.stderr)):
            reader = threading.Thread(target=read_stream, args=(index, stream), daemon=True)
            readers.append(reader)
            reader.start()
        if on_line:
            process.stdin.write(b'{"jsonrpc":"2.0","id":1,"method":"initialize","params":'
                                b'{"protocolVersion":"2024-11-05","capabilities":{},'
                                b'"clientInfo":{"name":"offline-bench-probe","version":"1"}}}\n')
            process.stdin.flush()
        else:
            process.stdin.close()
        while process.poll() is None:
            if overflow.is_set():
                failure = "trace_overflow"
                break
            if callback_errors:
                failure = "protocol_error"
                break
            if time.monotonic() - start >= timeout:
                failure = "timeout"
                break
            time.sleep(0.01)
        if failure is None and time.monotonic() - start >= timeout:
            failure = "timeout"
    finally:
        try:
            if process:
                if job:
                    try:
                        job.terminate()
                    finally:
                        if process.poll() is None:
                            process.kill()
                else:
                    try:
                        os.killpg(process.pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                process.wait(timeout=5)
                for reader in readers:
                    reader.join(timeout=2)
                if process.stdin and not process.stdin.closed:
                    process.stdin.close()
        finally:
            if job:
                job.close()
    if overflow.is_set():
        failure = "trace_overflow"
    if callback_errors and failure is None:
        failure = "protocol_error"
    return ProcessResult(process.returncode, time.monotonic() - start,
                         bytes(chunks[0]), bytes(chunks[1]), failure, overflow.is_set(),
                         tuple(str(error) for error in callback_errors))
