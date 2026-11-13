#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#include <windows.h>
#include <string>
#include <vector>

namespace
{
    bool FileExists(const std::wstring& path)
    {
        DWORD attrs = GetFileAttributesW(path.c_str());
        return attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY) == 0;
    }

    std::wstring GetExecutableDirectory()
    {
        std::vector<wchar_t> buffer(MAX_PATH);
        for (;;)
        {
            DWORD len = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
            if (len == 0)
                return L"";
            if (len < buffer.size() - 1)
            {
                std::wstring fullPath(buffer.data(), len);
                size_t slash = fullPath.find_last_of(L"\\/");
                return slash == std::wstring::npos ? L"" : fullPath.substr(0, slash);
            }
            buffer.resize(buffer.size() * 2);
        }
    }

    bool LaunchTarget(const std::wstring& exePath, const std::wstring& workDir, const wchar_t* extraArgs)
    {
        std::wstring cmdLine = L"\"" + exePath + L"\"";
        if (extraArgs && *extraArgs)
        {
            cmdLine += L" ";
            cmdLine += extraArgs;
        }

        std::vector<wchar_t> mutableCmd(cmdLine.begin(), cmdLine.end());
        mutableCmd.push_back(L'\0');

        STARTUPINFOW si{};
        si.cb = sizeof(si);
        PROCESS_INFORMATION pi{};

        if (!CreateProcessW(
                exePath.c_str(),
                mutableCmd.data(),
                nullptr,
                nullptr,
                FALSE,
                0,
                nullptr,
                workDir.c_str(),
                &si,
                &pi))
        {
            return false;
        }

        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return true;
    }
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR pCmdLine, int)
{
    const std::wstring baseDir = GetExecutableDirectory();
    if (baseDir.empty())
    {
        MessageBoxW(
            nullptr,
            L"Não foi possível localizar a pasta do YouLink.",
            L"YouLink",
            MB_OK | MB_ICONERROR);
        return 1;
    }

    const std::wstring appSubDir = baseDir + L"\\App";
    const std::wstring primaryExe = appSubDir + L"\\appmaui.blazor.exe";
    const std::wstring fallbackExe = baseDir + L"\\appmaui.blazor.exe";

    std::wstring targetExe;
    std::wstring workDir;
    if (FileExists(primaryExe))
    {
        targetExe = primaryExe;
        workDir = appSubDir;
    }
    else if (FileExists(fallbackExe))
    {
        targetExe = fallbackExe;
        workDir = baseDir;
    }
    else
    {
        MessageBoxW(
            nullptr,
            L"Não foi possível encontrar os arquivos internos do YouLink (pasta App).\n\n"
            L"Se você abriu este arquivo direto dentro do .zip, extraia toda a pasta 'YouLink' primeiro "
            L"(botão direito no .zip -> Extrair Tudo) antes de clicar em YouLink.exe,\n"
            L"ou baixe e execute o instalador YouLink-Setup-x64.exe.",
            L"YouLink — Extração necessária",
            MB_OK | MB_ICONWARNING);
        return 1;
    }

    if (!LaunchTarget(targetExe, workDir, pCmdLine))
    {
        MessageBoxW(
            nullptr,
            L"Não foi possível iniciar o YouLink. Tente extrair novamente os arquivos ou usar o instalador YouLink-Setup-x64.exe.",
            L"YouLink",
            MB_OK | MB_ICONERROR);
        return 1;
    }

    return 0;
}
