//
// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO
// THE IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.
//
// Copyright (c) Microsoft Corporation. All rights reserved.
//
// CCommandWindow retains Microsoft's event-source abstraction but listens to
// the PhoneUnlock named pipe instead of creating a simulated command window.
//

#pragma once

#include <windows.h>
#include <string>
#include "CSampleProvider.h"

class CCommandWindow
{
public:
    CCommandWindow();
    ~CCommandWindow();
    HRESULT Initialize(__in CSampleProvider *pProvider);
    BOOL GetConnectedStatus();

private:
    static DWORD WINAPI _ThreadProc(__in LPVOID lpParameter);
    void _Run();
    bool _ConnectAndRead();
    bool _WritePipeMessage(HANDLE hPipe, const std::wstring& text);
    bool _SendUnlockRequest(HANDLE hPipe);

    CSampleProvider            *_pProvider;        // Pointer to our owner.
    HANDLE                      _hThread;
    HANDLE                      _hStopEvent;
    volatile LONG               _fConnected;
};
