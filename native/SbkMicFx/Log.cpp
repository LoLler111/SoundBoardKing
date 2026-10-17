#include "Log.h"

#include <windows.h>
#include <shlobj.h>
#include <cstdarg>
#include <cstdio>

namespace sbk
{
    void Log(const wchar_t* format, ...)
    {
        wchar_t message[512];
        va_list args;
        va_start(args, format);
        _vsnwprintf_s(message, _TRUNCATE, format, args);
        va_end(args);

        PWSTR programData = nullptr;
        if (FAILED(SHGetKnownFolderPath(FOLDERID_ProgramData, 0, nullptr, &programData)))
            return;
        wchar_t path[MAX_PATH];
        swprintf_s(path, L"%s\\SoundBoardKing\\micfx.log", programData);
        CoTaskMemFree(programData);

        SYSTEMTIME time;
        GetLocalTime(&time);
        wchar_t line[640];
        const int length = swprintf_s(line, L"%04u-%02u-%02u %02u:%02u:%02u [%lu] %s\r\n",
                                      time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond,
                                      GetCurrentProcessId(), message);
        if (length <= 0)
            return;

        HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                  OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return;

        // Store as UTF-8 so the file opens cleanly in any editor.
        char utf8[1280];
        const int bytes = WideCharToMultiByte(CP_UTF8, 0, line, length, utf8, sizeof(utf8), nullptr, nullptr);
        DWORD written = 0;
        if (bytes > 0)
            WriteFile(file, utf8, static_cast<DWORD>(bytes), &written, nullptr);
        CloseHandle(file);
    }
}
