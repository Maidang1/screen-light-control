"""Capture only the Screen Light window, without moving the user's pointer."""
import ctypes as c
from ctypes import wintypes as w
import sys
from PIL import Image

user = c.WinDLL('user32', use_last_error=True)
gdi = c.WinDLL('gdi32', use_last_error=True)
user.SetProcessDPIAware()
user.GetWindowDC.argtypes = [w.HWND]; user.GetWindowDC.restype = w.HDC
user.GetWindowRect.argtypes = [w.HWND, c.POINTER(w.RECT)]
user.PrintWindow.argtypes = [w.HWND, w.HDC, w.UINT]
user.ReleaseDC.argtypes = [w.HWND, w.HDC]
gdi.CreateCompatibleDC.argtypes = [w.HDC]; gdi.CreateCompatibleDC.restype = w.HDC
gdi.CreateCompatibleBitmap.argtypes = [w.HDC, c.c_int, c.c_int]; gdi.CreateCompatibleBitmap.restype = w.HBITMAP
gdi.SelectObject.argtypes = [w.HDC, w.HANDLE]; gdi.SelectObject.restype = w.HANDLE
gdi.DeleteObject.argtypes = [w.HANDLE]; gdi.DeleteDC.argtypes = [w.HDC]

class Header(c.Structure):
    _fields_ = [('size',w.DWORD),('width',w.LONG),('height',w.LONG),('planes',w.WORD),('bits',w.WORD),('compression',w.DWORD),('image_size',w.DWORD),('x',w.LONG),('y',w.LONG),('used',w.DWORD),('important',w.DWORD)]
gdi.GetDIBits.argtypes = [w.HDC,w.HBITMAP,w.UINT,w.UINT,c.c_void_p,c.POINTER(Header),w.UINT]
found = []
@c.WINFUNCTYPE(w.BOOL,w.HWND,w.LPARAM)
def find(hwnd, data):
    title = c.create_unicode_buffer(256); user.GetWindowTextW(hwnd,title,256)
    if 'Screen Light' in title.value and user.IsWindowVisible(hwnd): found.append(hwnd)
    return True
user.EnumWindows(find,0)
if not found: raise SystemExit('No visible Screen Light window')
hwnd=found[-1]; rect=w.RECT(); user.GetWindowRect(hwnd,c.byref(rect))
width,height=rect.right-rect.left,rect.bottom-rect.top
dc=user.GetWindowDC(hwnd); memory=gdi.CreateCompatibleDC(dc); bitmap=gdi.CreateCompatibleBitmap(dc,width,height); old=gdi.SelectObject(memory,bitmap)
try:
    if not user.PrintWindow(hwnd,memory,2): raise RuntimeError('PrintWindow failed')
    gdi.SelectObject(memory,old)
    header=Header(); header.size=c.sizeof(header); header.width=width; header.height=-height; header.planes=1; header.bits=32
    buffer=c.create_string_buffer(width*height*4)
    if not gdi.GetDIBits(memory,bitmap,0,height,buffer,c.byref(header),0): raise RuntimeError('GetDIBits failed')
    Image.frombuffer('RGB',(width,height),buffer,'raw','BGRX',0,1).save(sys.argv[1])
    print(sys.argv[1],width,height)
finally:
    gdi.DeleteObject(bitmap); gdi.DeleteDC(memory); user.ReleaseDC(hwnd,dc)
