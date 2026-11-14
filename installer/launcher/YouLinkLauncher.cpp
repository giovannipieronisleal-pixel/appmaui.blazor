#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0600
#endif
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#include <windows.h>
#include <tlhelp32.h>
#include <algorithm>
#include <cwctype>
#include <string>
#include <vector>

namespace
{
    constexpr const wchar_t* kInstanceMutexName = L"Local\\YouLink_AppWindow_Mutex_v1";
    constexpr const wchar_t* kActivateEventName = L"Local\\YouLink_ActivateWindow_Event_v1";
    constexpr const wchar_t* kLauncherGuardName = L"Local\\YouLink_Launcher_Guard_v1";

    bool FileExists(const std::wstring& path)
    {
        DWORD attrs = GetFileAttributesW(path.c_str());
        return attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY) == 0;
    }

    std::wstring ToLowerCopy(std::wstring value)
    {
        std::transform(value.begin(), value.end(), value.begin(),
            [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
        return value;
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

    bool EqualsExePathOrName(DWORD pid, const std::wstring& expectedExeLower)
    {
        HANDLE hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (!hProc)
            return false;

        std::vector<wchar_t> pathBuf(MAX_PATH * 2);
        DWORD size = static_cast<DWORD>(pathBuf.size());
        BOOL ok = QueryFullProcessImageNameW(hProc, 0, pathBuf.data(), &size);
        CloseHandle(hProc);
        if (!ok || size == 0)
            return false;

        std::wstring actualLower = ToLowerCopy(std::wstring(pathBuf.data(), size));
        if (actualLower == expectedExeLower)
            return true;
        const std::wstring suffix = L"\\appmaui.blazor.exe";
        return actualLower.size() >= suffix.size() &&
            actualLower.compare(actualLower.size() - suffix.size(), suffix.size(), suffix) == 0;
    }

    struct WindowSearchContext
    {
        std::wstring expectedExeLower;
        HWND foundHwnd = nullptr;
        DWORD foundPid = 0;
    };

    BOOL CALLBACK EnumTopLevelWindowProc(HWND hwnd, LPARAM lParam)
    {
        auto* ctx = reinterpret_cast<WindowSearchContext*>(lParam);
        if (!IsWindow(hwnd))
            return TRUE;

        if (!IsWindowVisible(hwnd) && !IsIconic(hwnd))
            return TRUE;

        if (GetWindow(hwnd, GW_OWNER) != nullptr)
            return TRUE;

        DWORD pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        if (pid == 0 || pid == GetCurrentProcessId())
            return TRUE;

        if (EqualsExePathOrName(pid, ctx->expectedExeLower))
        {
            ctx->foundHwnd = hwnd;
            ctx->foundPid = pid;
            return FALSE;
        }

        return TRUE;
    }

    bool HasRunningAppProcess(const std::wstring& expectedExeLower, DWORD& outPid)
    {
        outPid = 0;
        HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE)
            return false;

        PROCESSENTRY32W pe{};
        pe.dwSize = sizeof(pe);
        bool found = false;

        if (Process32FirstW(snap, &pe))
        {
            do
            {
                if (_wcsicmp(pe.szExeFile, L"appmaui.blazor.exe") == 0 &&
                    EqualsExePathOrName(pe.th32ProcessID, expectedExeLower))
                {
                    outPid = pe.th32ProcessID;
                    found = true;
                    break;
                }
            } while (Process32NextW(snap, &pe));
        }

        CloseHandle(snap);
        return found;
    }

    void SignalActivationEvent()
    {
        HANDLE hEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, kActivateEventName);
        if (hEvent)
        {
            SetEvent(hEvent);
            CloseHandle(hEvent);
        }
    }

    bool TryActivateExistingInstance(const std::wstring& targetExe)
    {
        const std::wstring expectedExeLower = ToLowerCopy(targetExe);

        // Verifica tanto o mutex do app quanto se o processo já foi disparado há poucos milissegundos
        bool instanceExists = false;
        HANDLE hMutex = OpenMutexW(SYNCHRONIZE, FALSE, kInstanceMutexName);
        if (hMutex)
        {
            DWORD waitRes = WaitForSingleObject(hMutex, 0);
            if (waitRes == WAIT_TIMEOUT)
            {
                instanceExists = true;
            }
            else if (waitRes == WAIT_OBJECT_0 || waitRes == WAIT_ABANDONED)
            {
                ReleaseMutex(hMutex);
            }
            CloseHandle(hMutex);
        }

        DWORD runningPid = 0;
        if (!instanceExists && HasRunningAppProcess(expectedExeLower, runningPid))
        {
            instanceExists = true;
        }

        if (!instanceExists)
            return false;

        SignalActivationEvent();

        // Caso a instância principal ainda esteja finalizando o boot, aguarda brevemente a janela surgir para focar
        for (int attempt = 0; attempt < 8; ++attempt)
        {
            WindowSearchContext ctx{ expectedExeLower };
            EnumWindows(EnumTopLevelWindowProc, reinterpret_cast<LPARAM>(&ctx));
            if (ctx.foundHwnd)
            {
                if (ctx.foundPid != 0)
                    AllowSetForegroundWindow(ctx.foundPid);

                ShowWindow(ctx.foundHwnd, IsIconic(ctx.foundHwnd) ? SW_RESTORE : SW_SHOW);
                SetForegroundWindow(ctx.foundHwnd);
                SignalActivationEvent();
                return true;
            }

            DWORD probePid = 0;
            if (!HasRunningAppProcess(expectedExeLower, probePid))
            {
                // O processo anterior acabou de fechar (ex.: reinício de atualização) — permite abrir o novo
                return false;
            }

            Sleep(120);
        }

        if (runningPid != 0)
            AllowSetForegroundWindow(runningPid);
        SignalActivationEvent();
        return true;
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

        WaitForInputIdle(pi.hProcess, 1500);
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

    HANDLE hGuard = CreateMutexW(nullptr, FALSE, kLauncherGuardName);
    if (hGuard)
    {
        WaitForSingleObject(hGuard, 3000);
    }

    if (TryActivateExistingInstance(targetExe))
    {
        if (hGuard)
        {
            ReleaseMutex(hGuard);
            CloseHandle(hGuard);
        }
        return 0;
    }

    const bool launched = LaunchTarget(targetExe, workDir, pCmdLine);
    if (hGuard)
    {
        ReleaseMutex(hGuard);
        CloseHandle(hGuard);
    }

    if (!launched)
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
