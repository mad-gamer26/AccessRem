# AssistBridge: detecting windows this copy of NVDA cannot operate.
# Copyright (C) 2026 AssistBridge contributors.
# This file is covered by the GNU General Public License, version 2 or later.

"""Detect when the foreground window belongs to a more privileged process.

Windows' User Interface Privilege Isolation stops a process from sending input to, or reading,
windows of a process with a higher integrity level (for example a program running as administrator),
unless the process has UI Access. A portable copy of NVDA has no UI Access, so it can neither read
nor control such windows. Pure ctypes so that it can be tested outside NVDA.
"""

import ctypes
from ctypes import wintypes

_user32 = ctypes.WinDLL("user32", use_last_error=True)
_kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
_advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
TOKEN_QUERY = 0x0008
TOKEN_INTEGRITY_LEVEL = 25
TOKEN_UI_ACCESS = 26
ERROR_ACCESS_DENIED = 5

SECURITY_MANDATORY_MEDIUM_RID = 0x2000
SECURITY_MANDATORY_HIGH_RID = 0x3000

_user32.GetForegroundWindow.restype = wintypes.HWND
_user32.GetWindowThreadProcessId.argtypes = (wintypes.HWND, ctypes.POINTER(wintypes.DWORD))
_user32.GetWindowThreadProcessId.restype = wintypes.DWORD
_kernel32.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
_kernel32.OpenProcess.restype = wintypes.HANDLE
_kernel32.GetCurrentProcess.restype = wintypes.HANDLE
_kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
_advapi32.OpenProcessToken.argtypes = (wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE))
_advapi32.GetTokenInformation.argtypes = (
	wintypes.HANDLE,
	ctypes.c_int,
	ctypes.c_void_p,
	wintypes.DWORD,
	ctypes.POINTER(wintypes.DWORD),
)
_advapi32.GetSidSubAuthorityCount.argtypes = (ctypes.c_void_p,)
_advapi32.GetSidSubAuthorityCount.restype = ctypes.POINTER(ctypes.c_ubyte)
_advapi32.GetSidSubAuthority.argtypes = (ctypes.c_void_p, wintypes.DWORD)
_advapi32.GetSidSubAuthority.restype = ctypes.POINTER(wintypes.DWORD)


def _tokenIntegrityAndUiAccess(process: int) -> tuple[int | None, bool | None, int]:
	"""(integrity RID, has UI Access, error) for a process handle; None where unknown."""
	token = wintypes.HANDLE()
	if not _advapi32.OpenProcessToken(process, TOKEN_QUERY, ctypes.byref(token)):
		return None, None, ctypes.get_last_error()
	try:
		size = wintypes.DWORD()
		_advapi32.GetTokenInformation(token, TOKEN_INTEGRITY_LEVEL, None, 0, ctypes.byref(size))
		buffer = ctypes.create_string_buffer(size.value)
		integrity = None
		if _advapi32.GetTokenInformation(token, TOKEN_INTEGRITY_LEVEL, buffer, size, ctypes.byref(size)):
			# TOKEN_MANDATORY_LABEL begins with a SID_AND_ATTRIBUTES whose first field points to the SID.
			sid = ctypes.cast(buffer, ctypes.POINTER(ctypes.c_void_p))[0]
			count = _advapi32.GetSidSubAuthorityCount(sid)[0]
			integrity = _advapi32.GetSidSubAuthority(sid, count - 1)[0]
		uiAccess = wintypes.DWORD()
		got = _advapi32.GetTokenInformation(
			token,
			TOKEN_UI_ACCESS,
			ctypes.byref(uiAccess),
			ctypes.sizeof(uiAccess),
			ctypes.byref(size),
		)
		return integrity, bool(uiAccess.value) if got else None, 0
	finally:
		_kernel32.CloseHandle(token)


def ownPrivileges() -> tuple[int, bool]:
	"""This process's integrity level and whether it has UI Access."""
	integrity, uiAccess, _error = _tokenIntegrityAndUiAccess(_kernel32.GetCurrentProcess())
	return integrity or SECURITY_MANDATORY_MEDIUM_RID, bool(uiAccess)


def windowProcessIntegrity(hwnd: int) -> tuple[int, int | None]:
	"""(process id, integrity RID) of the process owning a window.

	Returns SECURITY_MANDATORY_HIGH_RID when the process token cannot be read because access is denied:
	that is what a standard process sees for an elevated one. None when the window is unknown.
	"""
	pid = wintypes.DWORD()
	_user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
	if not pid.value:
		return 0, None
	return pid.value, processIntegrity(pid.value)


def processIntegrity(pid: int) -> int | None:
	"""Integrity RID of a process, or HIGH when access to it is denied (as for elevated processes)."""
	process = _kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
	if not process:
		denied = ctypes.get_last_error() == ERROR_ACCESS_DENIED
		return SECURITY_MANDATORY_HIGH_RID if denied else None
	try:
		integrity, _uiAccess, error = _tokenIntegrityAndUiAccess(process)
		if integrity is None and error == ERROR_ACCESS_DENIED:
			return SECURITY_MANDATORY_HIGH_RID
		return integrity
	finally:
		_kernel32.CloseHandle(process)


def foregroundIsOutOfReach() -> tuple[bool, int, int]:
	"""Whether the foreground window is more privileged than this process can operate.

	Returns (outOfReach, foreground window handle, owning process id).
	"""
	hwnd = _user32.GetForegroundWindow() or 0
	if not hwnd:
		return False, 0, 0
	ownIntegrity, ownUiAccess = ownPrivileges()
	if ownUiAccess:
		return False, hwnd, 0
	pid, integrity = windowProcessIntegrity(hwnd)
	return bool(integrity is not None and integrity > ownIntegrity), hwnd, pid


if __name__ == "__main__":
	# Manual check: report on the window that has focus five seconds from now.
	import time

	time.sleep(5)
	print(ownPrivileges(), foregroundIsOutOfReach())
