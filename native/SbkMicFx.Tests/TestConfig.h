#pragma once

// Force-included into every file of the harness build: the APO uses a per-session shared
// memory name, so the harness and the C# producer can talk without administrator rights.
#define SBK_TEST_SHARED_MEMORY_NAME L"Local\\SoundBoardKing.MicFx.test"
