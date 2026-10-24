"""Read native brightness channels; --write-test briefly changes and restores brightness."""
import ctypes as c
from ctypes import wintypes as w
import json
import sys
import time
from pathlib import Path

user = c.WinDLL('user32', use_last_error=True)
dx = c.WinDLL('dxva2', use_last_error=True)

class Physical(c.Structure):
    _fields_ = [('handle', w.HANDLE), ('description', w.WCHAR * 128)]

class MonitorInfo(c.Structure):
    _fields_ = [('size', w.DWORD), ('monitor', w.RECT), ('work', w.RECT), ('flags', w.DWORD), ('device', w.WCHAR * 32)]

callback_type = c.WINFUNCTYPE(w.BOOL, w.HANDLE, w.HDC, c.POINTER(w.RECT), w.LPARAM)
user.EnumDisplayMonitors.argtypes = [w.HDC, c.c_void_p, callback_type, w.LPARAM]
user.GetMonitorInfoW.argtypes = [w.HANDLE, c.POINTER(MonitorInfo)]
dx.GetNumberOfPhysicalMonitorsFromHMONITOR.argtypes = [w.HANDLE, c.POINTER(w.DWORD)]
dx.GetPhysicalMonitorsFromHMONITOR.argtypes = [w.HANDLE, w.DWORD, c.POINTER(Physical)]
dx.GetMonitorBrightness.argtypes = [w.HANDLE, c.POINTER(w.DWORD), c.POINTER(w.DWORD), c.POINTER(w.DWORD)]
dx.SetMonitorBrightness.argtypes = [w.HANDLE, w.DWORD]
dx.GetVCPFeatureAndVCPFeatureReply.argtypes = [w.HANDLE, w.BYTE, c.c_void_p, c.POINTER(w.DWORD), c.POINTER(w.DWORD)]
dx.SetVCPFeature.argtypes = [w.HANDLE, w.BYTE, w.DWORD]
dx.DestroyPhysicalMonitor.argtypes = [w.HANDLE]

def read(handle, vcp):
    low, current, high = w.DWORD(), w.DWORD(), w.DWORD()
    c.set_last_error(0)
    ok = dx.GetVCPFeatureAndVCPFeatureReply(handle, 0x10, None, c.byref(current), c.byref(high)) if vcp else dx.GetMonitorBrightness(handle, c.byref(low), c.byref(current), c.byref(high))
    return dict(ok=bool(ok), error=c.get_last_error() if not ok else 0, min=low.value, current=current.value, max=high.value)

report = []
@callback_type
def enum(monitor, hdc, rect, data):
    info = MonitorInfo(); info.size = c.sizeof(info); user.GetMonitorInfoW(monitor, c.byref(info))
    count = w.DWORD()
    if not dx.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, c.byref(count)): return True
    physical = (Physical * count.value)()
    if not dx.GetPhysicalMonitorsFromHMONITOR(monitor, count, physical): return True
    for item in physical:
        entry = dict(device=info.device, name=item.description, handle=item.handle, high_level=read(item.handle, False), vcp=read(item.handle, True))
        report.append(entry)
        try:
            if '--write-test' in sys.argv:
                for vcp in (False, True):
                    original = read(item.handle, vcp)
                    if not original['ok'] or original['max'] <= original['min']: continue
                    step = max(1, round((original['max'] - original['min']) * .05))
                    target = original['current'] - step if original['current'] >= original['min'] + step else original['current'] + step
                    setter = (lambda value: dx.SetVCPFeature(item.handle, 0x10, value)) if vcp else (lambda value: dx.SetMonitorBrightness(item.handle, value))
                    trial = dict(target=target)
                    entry['vcp_trial' if vcp else 'high_trial'] = trial
                    try:
                        trial['write_ok'] = bool(setter(target))
                        trial['write_error'] = c.get_last_error() if not trial['write_ok'] else 0
                        time.sleep(.8)
                        trial['readback'] = read(item.handle, vcp)
                    finally:
                        trial['restore_ok'] = bool(setter(original['current']))
                        time.sleep(.8)
                        trial['restored'] = read(item.handle, vcp)
        finally:
            dx.DestroyPhysicalMonitor(item.handle)
    return True

if __name__ == '__main__':
    user.EnumDisplayMonitors(None, None, enum, 0)
    output = json.dumps(report, ensure_ascii=False, indent=2)
    Path('test-output').mkdir(exist_ok=True)
    Path('test-output/native-probe.json').write_text(output, encoding='utf-8')
    print(output)
