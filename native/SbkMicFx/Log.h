#pragma once

namespace sbk
{
    // Appends a line to %ProgramData%\SoundBoardKing\micfx.log. Never call from APOProcess.
    // The installer creates the folder with write access for LOCAL SERVICE; if it is missing
    // the call silently does nothing.
    void Log(const wchar_t* format, ...);
}
