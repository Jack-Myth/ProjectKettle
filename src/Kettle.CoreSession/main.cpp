#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <objidl.h>
#include <propidl.h>
#include <gdiplus.h>
#include <string>
#include <vector>
#include <regex>
#include <mutex>
#include <condition_variable>
#include <thread>
#include <atomic>
#include <filesystem>
#include <fstream>
#include <stdexcept>
#include <algorithm>
#include <chrono>
#include <set>
#include <tlhelp32.h>

namespace fs = std::filesystem;
using namespace std::chrono_literals;
std::wstring self, root, steam, pipeToken, jobDirectory, gameName;
HANDLE consoleInput{}, consoleOutput{}, steamProcess{}, jobHandle{},
    rawClient = INVALID_HANDLE_VALUE;
HPCON console{};
HWND window{}, statusLabel{}, stopButton{};
std::mutex outputMutex, rawMutex, writeMutex;
std::mutex terminalMutex;
std::atomic<bool> abandonedLogin{false};
std::string output;
std::atomic<bool> runtime{false}, alive{true}, busy{false}, stopRequested{false},
    runningObserved{false}, uploadPending{false}, stopping{false};
std::atomic<DWORD> frontendPid{};
std::mutex stateMutex;
std::wstring recoveryDirectory, currentMessage, conflictTicket, conflictAnswer;
HANDLE frontendProcess{};
bool normalClose = false, restartPaused = false;
bool frontendNeeded = true;
std::vector<ULONGLONG> restarts;
std::condition_variable conflictChanged;
enum class Phase
{
    idle,
    preparing,
    starting,
    running,
    uploading,
    completed
};
std::atomic<Phase> phase{Phase::idle};

unsigned appId{};
std::wstring launchIndex;

void trace(const char *message)
{
#ifdef KETTLE_CORE_SESSION_DEBUG
    try
    {
        fs::create_directories(root + L"/data/diagnostics");
        auto file = root + L"/data/diagnostics/core-session-" +
                    std::to_wstring(GetCurrentProcessId()) + L".log";
        std::ofstream stream(fs::path(file), std::ios::app);
        stream << GetTickCount64() << " " << message << "\n";
    }
    catch (...)
    {
    }
#else
    (void)message;
#endif
}

std::wstring wide(const std::string &s)
{
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), (int)s.size(), nullptr, 0);
    if (!n && !s.empty())
        throw std::runtime_error("Invalid UTF-8.");
    std::wstring v(n, 0);
    MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), v.data(), n);
    return v;
}

std::string utf8(const std::wstring &s)
{
    int n = WideCharToMultiByte(CP_UTF8, 0, s.data(), (int)s.size(), nullptr, 0, nullptr, nullptr);
    std::string v(n, 0);
    WideCharToMultiByte(CP_UTF8, 0, s.data(), (int)s.size(), v.data(), n, nullptr, nullptr);
    return v;
}

#include "localization.h"

std::wstring quote(const std::wstring &s)
{
    std::wstring r = L"\"";
    size_t slashes = 0;
    for (auto c : s)
    {
        if (c == L'\\')
        {
            ++slashes;
            continue;
        }
        r.append(c == L'"' ? slashes * 2 + 1 : slashes, L'\\');
        slashes = 0;
        r += c;
    }
    r.append(slashes * 2, L'\\');
    return r + L"\"";
}

void check(bool ok, const char *message)
{
    if (!ok)
        throw std::runtime_error(std::string(message) + " (" + std::to_string(GetLastError()) +
                                 ")");
}

void writeAll(HANDLE h, const char *data, size_t size)
{
    while (size)
    {
        DWORD written{};
        check(WriteFile(h, data, (DWORD)std::min<size_t>(size, 65536), &written, nullptr) &&
                  written,
              errorText("PipeWrite").c_str());
        data += written;
        size -= written;
    }
}

void writeRaw(HANDLE h, const char *data, DWORD size)
{
    OVERLAPPED operation{};
    operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    check(operation.hEvent != nullptr, errorText("PipeEvent").c_str());
    DWORD written{};
    bool ok = WriteFile(h, data, size, &written, &operation) != FALSE;
    if (!ok && GetLastError() == ERROR_IO_PENDING)
        ok = GetOverlappedResult(h, &operation, &written, TRUE) != FALSE;
    CloseHandle(operation.hEvent);
    check(ok && written == size, errorText("RawPipeWrite").c_str());
}

void atomicFile(const std::wstring &path, const std::string &text)
{
    auto temporary = path + L".tmp";
    HANDLE h = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    check(h != INVALID_HANDLE_VALUE, errorText("WriteJournal").c_str());
    try
    {
        writeAll(h, text.data(), text.size());
        check(FlushFileBuffers(h), errorText("FlushJournal").c_str());
    }
    catch (...)
    {
        CloseHandle(h);
        throw;
    }
    CloseHandle(h);
    check(MoveFileExW(temporary.c_str(), path.c_str(),
                      MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH),
          errorText("CommitJournal").c_str());
}

std::wstring ini(const std::wstring &file, const wchar_t *section, const wchar_t *key)
{
    std::vector<wchar_t> b(16384);
    DWORD n = GetPrivateProfileStringW(section, key, L"", b.data(), (DWORD)b.size(), file.c_str());
    if (n >= b.size() - 1)
        throw std::runtime_error(errorText("OversizedField").c_str());
    return {b.data(), n};
}

void display(const std::wstring &text)
{
    {
        std::lock_guard lock(stateMutex);
        currentMessage = text;
    }
    auto copy = new std::wstring(text);
    if (!PostMessageW(window, WM_APP + 1, 0, (LPARAM)copy))
        delete copy;
}

void setPhase(Phase value)
{
    phase = value;
    const wchar_t *name = value == Phase::preparing   ? L"preparing"
                          : value == Phase::starting  ? L"starting"
                          : value == Phase::running   ? L"running"
                          : value == Phase::uploading ? L"uploading"
                                                      : L"completed";
    atomicFile(jobDirectory + L"/native-state", utf8(name));
    trace(utf8(name).c_str());
    std::wstring label = value == Phase::preparing   ? text("Preparing").c_str()
                         : value == Phase::starting  ? text("Starting").c_str()
                         : value == Phase::running   ? text("Running").c_str()
                         : value == Phase::uploading ? text("Uploading").c_str()
                                                     : text("Completed").c_str();
    display(gameName + L" — " + label);
}

void sendSteam(const std::string &value)
{
    std::lock_guard lock(writeMutex);
    writeAll(consoleInput, value.data(), value.size());
}

bool requestStop()
{
    std::lock_guard lock(outputMutex);
    if (phase != Phase::running || !busy || uploadPending)
        return false;
    if (!stopping.exchange(true))
    {
        stopRequested = true;
        display(gameName + L" — " + text("Stopping"));
        PostMessageW(window, WM_APP + 9, 0, 0);
    }
    return true;
}

// Streaming terminal normalization; control sequences may span arbitrary pipe reads.
struct Normalizer
{
    int escape{}, column{}, number{};
    bool cr{}, simple{};

    void append(const char *bytes, size_t length, std::string &text)
    {
        for (size_t i = 0; i < length; ++i)
        {
            unsigned char c = (unsigned char)bytes[i];
            if (escape == 1)
            {
                escape = c == '[' ? 2 : c == ']' ? 3 : 0;
                number = 0;
                simple = true;
                continue;
            }
            if (escape == 2)
            {
                if (c >= '@' && c <= '~')
                {
                    if (c == 'H' || c == 'f')
                    {
                        text += '\n';
                        column = 0;
                        cr = false;
                    }
                    else if (simple && (c == 'C' || c == 'a' || c == 'G' || c == '`'))
                    {
                        int distance = std::max(1, number);
                        if (c == 'G' || c == '`')
                            distance = std::max(0, distance - 1 - column);
                        distance = std::min(distance, 4096);
                        text.append(distance, ' ');
                        column += distance;
                        cr = false;
                    }
                    escape = 0;
                }
                else if (c >= '0' && c <= '9')
                    number = std::min(4097, number * 10 + c - '0');
                else
                    simple = false;
                continue;
            }
            if (escape == 3)
            {
                if (c == 7)
                    escape = 0;
                else if (c == 27)
                    escape = 4;
                continue;
            }
            if (escape == 4)
            {
                escape = c == '\\' ? 0 : 3;
                continue;
            }
            if (c == 27)
            {
                escape = 1;
                continue;
            }
            if (c == '\r')
            {
                text += '\n';
                cr = true;
                column = 0;
                continue;
            }
            if (c == '\n' && cr)
            {
                cr = false;
                continue;
            }
            cr = false;
            if (c == '\b')
            {
                if (!text.empty())
                    text.pop_back();
                column = std::max(0, column - 1);
            }
            else if (c >= 32 || c == '\n' || c == '\t')
            {
                text += (char)c;
                column = c == '\n'   ? 0
                         : c == '\t' ? (column / 8 + 1) * 8
                                     : std::min(4096, column + 1);
            }
        }
    }
};

std::string trim(std::string s)
{
    auto first = s.find_first_not_of(" \t\r\n");
    if (first == std::string::npos)
        return {};
    return s.substr(first, s.find_last_not_of(" \t\r\n") - first + 1);
}

// Accessed only while holding outputMutex; independent of command responses.
struct GameEvents
{
    std::set<std::pair<unsigned, DWORD>> active;
    std::set<DWORD> reportedPids, helperActive, helperRemoved;
    std::string launchFailure;
    unsigned target{};
    bool armed{}, seen{}, failed{}, uncertain{}, stopSent{};

    void begin(unsigned id)
    {
        reportedPids.clear();
        target = id;
        armed = true;
        seen = failed = stopSent = false;
        launchFailure.clear();
    }

    bool processesExited(const std::set<DWORD> &existing) const
    {
        if (reportedPids.empty())
            return false;
        for (auto pid : reportedPids)
            if (existing.count(pid))
                return false;
        return true;
    }

    bool idle()
    {
        if (stopSent)
        {
            // Read the process list only. Never open or terminate game processes.
            HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == INVALID_HANDLE_VALUE)
                throw std::runtime_error(errorText("ProcessList").c_str());
            PROCESSENTRY32W entry{};
            entry.dwSize = sizeof(entry);
            std::set<DWORD> existing;
            bool found = Process32FirstW(snapshot, &entry) != FALSE;
            while (found)
            {
                existing.insert(entry.th32ProcessID);
                found = Process32NextW(snapshot, &entry) != FALSE;
            }
            auto error = GetLastError();
            CloseHandle(snapshot);
            if (error != ERROR_NO_MORE_FILES || existing.empty())
                throw std::runtime_error(errorText("IncompleteProcesses").c_str());
            if (!processesExited(existing))
                return false;
            // Missing removed notifications no longer prevent post-Stop synchronization.
            for (auto pid : reportedPids)
                active.erase({target, pid});
        }
        return active.empty();
    }

    bool helperExited(DWORD pid, bool registered) const
    {
        return (!registered && !helperActive.count(pid)) || helperRemoved.count(pid);
    }

    void line(const std::string &text)
    {
        auto start = text.find("Game process ");
        if (start == std::string::npos)
        {
            if (armed && text.find("AppId " + std::to_string(target) + " launch failed") !=
                             std::string::npos)
            {
                failed = true;
                launchFailure = text;
            }
            return;
        }
        auto notice = text.substr(start);
        const bool helper = notice.find("Kettle.CloudHost.exe") != std::string::npos;
        static const std::regex event(
            R"(^Game process (added|updated|removed)\s*:\s*AppID\s+([0-9]+)\s+.*,[ \t]*ProcID[ \t]+([0-9]+)(?:[ \t]*,[ \t]*IP[ \t]+[^\s]+)?[ \t]*$)");
        std::smatch match;
        if (text.size() >= 4095 || !std::regex_match(notice, match, event))
        {
            uncertain = true;
            return;
        }
        try
        {
            auto id = std::stoull(match[2]), pid = std::stoull(match[3]);
            if (!id || id > UINT_MAX || !pid || pid > INT_MAX)
            {
                uncertain = true;
                return;
            }
            if (helper)
            {
                if (match[1] == "removed")
                {
                    helperActive.erase((DWORD)pid);
                    helperRemoved.insert((DWORD)pid);
                }
                else
                {
                    helperRemoved.erase((DWORD)pid);
                    helperActive.insert((DWORD)pid);
                }
                return;
            }
            auto key = std::make_pair((unsigned)id, (DWORD)pid);
            if (match[1] == "removed")
            {
                if (!active.erase(key) && armed && id == target && !reportedPids.count((DWORD)pid))
                    uncertain = true;
            }
            else
            {
                active.insert(key);
                if (armed && id == target)
                {
                    seen = true;
                    reportedPids.insert((DWORD)pid);
                }
            }
            trace(notice.c_str());
        }
        catch (...)
        {
            uncertain = true;
        }
    }
};

GameEvents events;

void waitIdle()
{
    std::lock_guard lock(outputMutex);
    if (!alive || events.uncertain || !events.idle() ||
        output.find("Game process ") != std::string::npos)
        throw std::runtime_error(errorText("NotIdle").c_str());
}

std::wstring newId()
{
    GUID id{};
    check(SUCCEEDED(CoCreateGuid(&id)), errorText("RequestId").c_str());
    wchar_t text[40];
    StringFromGUID2(id, text, 40);
    std::wstring s;
    for (auto c : std::wstring(text))
        if (iswxdigit(c))
            s += (wchar_t)towlower(c);
    return s;
}

HANDLE launch(const std::wstring &exe, const std::wstring &args, const std::wstring &directory,
              DWORD flags = CREATE_NO_WINDOW, bool cloud = false)
{
    STARTUPINFOW si{sizeof(si)};
    PROCESS_INFORMATION pi{};
    auto command = quote(exe) + L" " + args;
    std::vector<wchar_t> environment;
    if (cloud)
    {
        auto source = GetEnvironmentStringsW();
        check(source != nullptr, errorText("Environment").c_str());
        for (auto p = source; *p; p += wcslen(p) + 1)
        {
            std::wstring entry(p);
            if (_wcsnicmp(p, L"DOTNET_GCSegmentSize=", 21) == 0 ||
                _wcsnicmp(p, L"COMPlus_GCSegmentSize=", 22) == 0 ||
                _wcsnicmp(p, L"KETTLE_CLOUD_NOGC_SOH_MB=", 25) == 0)
                continue;
            environment.insert(environment.end(), entry.begin(), entry.end());
            environment.push_back(0);
        }
        FreeEnvironmentStringsW(source);
        for (auto value : {L"DOTNET_GCSegmentSize=8000000", L"KETTLE_CLOUD_NOGC_SOH_MB=64"})
        {
            environment.insert(environment.end(), value, value + wcslen(value) + 1);
        }
        environment.push_back(0);
        flags |= CREATE_UNICODE_ENVIRONMENT;
    }
    check(CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, flags,
                         cloud ? environment.data() : nullptr, directory.c_str(), &si, &pi),
          errorText("ProcessLaunch").c_str());
    CloseHandle(pi.hThread);
    return pi.hProcess;
}

void watchFrontend(DWORD pid)
{
    auto handle = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    check(handle != nullptr, errorText("WatchFrontend").c_str());
    std::lock_guard lock(stateMutex);
    if (frontendProcess)
        CloseHandle(frontendProcess);
    frontendProcess = handle;
    normalClose = false;
}

void superviseFrontend()
{
    while (alive)
    {
        std::this_thread::sleep_for(1s);
        try
        {
            std::lock_guard lock(stateMutex);
            if (!frontendNeeded || normalClose || restartPaused ||
                (!frontendProcess && recoveryDirectory.empty()) ||
                (frontendProcess && WaitForSingleObject(frontendProcess, 0) != WAIT_OBJECT_0))
                continue;
            if (recoveryDirectory.empty())
            {
                // Authentication has not been registered. Never attach a replacement UI
                // as authenticated, and never leave the hidden login broker orphaned.
                if (!runtime && !busy)
                {
                    normalClose = true;
                    abandonedLogin = true;
                    trace("frontend exited before login registration; closing login session");
                    PostMessageW(window, WM_APP + 8, 0, 0);
                }
                continue;
            }
            auto now = GetTickCount64();
            std::erase_if(restarts, [now](auto time) { return now - time > 60000; });
            if (restarts.size() >= 3)
            {
                restartPaused = true;
                PostMessageW(window, WM_APP + 7, 0, 0);
                continue;
            }
            if (frontendProcess)
                CloseHandle(frontendProcess);
            frontendProcess = nullptr;
            restarts.push_back(now);
            frontendProcess = launch(root + L"/Kettle.exe",
                                     L"--core-session-resume " + quote(recoveryDirectory), root, 0);
        }
        catch (const std::exception &e)
        {
            trace(e.what());
        }
    }
}

std::string hex(const std::wstring &value)
{
    const char *digits = "0123456789abcdef";
    std::string result;
    for (unsigned char c : utf8(value))
    {
        result += digits[c >> 4];
        result += digits[c & 15];
    }
    return result;
}

bool synchronize(const wchar_t *action)
{
    std::wstring choice = L"none", ticket = L"-";
    for (;;)
    {
        auto id = newId();
        auto args = L"--sync " + quote(jobDirectory) + L" " + action + L" " + choice + L" " +
                    quote(ticket) + L" " + id;
        auto process = launch(root + L"/cloud-host/Kettle.CloudHost.exe", args,
                              fs::path(steam).parent_path(), CREATE_NO_WINDOW, true);
        auto helperPid = GetProcessId(process);
        WaitForSingleObject(process, INFINITE);
        DWORD code = 1;
        GetExitCodeProcess(process, &code);
        CloseHandle(process);
        auto result = jobDirectory + L"/result.ini";
        if (ini(result, L"result", L"RequestId") != id)
            throw std::runtime_error(errorText("MissingResult").c_str());
        // OS process exit precedes SteamCMD's asynchronous removal notification.
        // Workers that never initialized SteamAPI (e.g. no cloud support) emit no event.
        const bool registered = ini(result, L"result", L"SteamRegistered") != L"0";
        auto deadline = std::chrono::steady_clock::now() + 30s;
        for (;;)
        {
            {
                std::lock_guard lock(outputMutex);
                if (events.helperExited(helperPid, registered))
                {
                    events.helperRemoved.erase(helperPid);
                    break;
                }
            }
            if (!alive || std::chrono::steady_clock::now() >= deadline)
                throw std::runtime_error(errorText("HelperRemoval").c_str());
            std::this_thread::sleep_for(100ms);
        }
        auto status = ini(result, L"result", L"Status");
        if (code == 0 && status == L"ok")
            return true;

        if (code != 10 || status != L"conflict")
            throw std::runtime_error(errorText("CloudFailure"));
        ticket = ini(result, L"result", L"Ticket");
        if (ticket.empty())
            throw std::runtime_error(errorText("ConflictTicket").c_str());
        display(text("CloudConflict"));
        std::unique_lock lock(stateMutex);
        conflictTicket = ticket;
        conflictAnswer.clear();
        frontendNeeded = true;
        conflictChanged.wait(lock, [] { return !conflictAnswer.empty(); });
        choice = conflictAnswer;
        conflictTicket.clear();
        conflictAnswer.clear();
        if (choice == L"cancel")
            return false;
    }
}

void showError(const std::exception &e)
{
    {
        std::lock_guard lock(stateMutex);
        frontendNeeded = true;
    }
    PostMessageW(window, WM_APP + 7, 0, 0);
    display(gameName + L"\n" + wide(e.what()) + L"\n" + text("KeepOpen"));
}

void returnToMain(bool launchFailed = false)
{
    if (!launchFailed)
        waitIdle();
    setPhase(Phase::completed);
    if (launchFailed)
        display(gameName + L" — " + text("LaunchFailed"));
    busy = false;
    {
        std::lock_guard lock(stateMutex);
        frontendNeeded = true;
        if (!recoveryDirectory.empty())
            return;
    }
    auto args = L"--core-session --core-session-resume " + quote(jobDirectory);
    auto process = launch(root + L"/Kettle.exe", args, root, 0);
    CloseHandle(process);
}

void runGame(bool retry)
{
    if (busy.exchange(true))
        return;
    try
    {
        if (retry && phase == Phase::completed)
        {
            returnToMain();
            return;
        }
        if (!retry || phase == Phase::preparing)
        {
            setPhase(Phase::preparing);
            waitIdle();
            if (!synchronize(L"prepare"))
            {
                busy = false;
                display(text("PreparationPaused").c_str());
                return;
            }
            waitIdle();
            setPhase(Phase::starting);
            auto command = "app_launch " + std::to_string(appId) +
                           (launchIndex.empty() ? "" : " " + utf8(launchIndex));
            // One-shot mutation: never resend app_launch after an uncertain response.
            atomicFile(jobDirectory + L"/launch-issued", utf8(fs::path(jobDirectory).filename()));
            {
                std::lock_guard lock(outputMutex);
                if (events.uncertain || !events.active.empty() ||
                    output.find("Game process ") != std::string::npos)
                    throw std::runtime_error(errorText("ProcessChanged").c_str());
                events.begin(appId);
                sendSteam(command + "\r");
            }
        }
        if (!uploadPending)
        {
            auto deadline = std::chrono::steady_clock::now() + 60s;
            int empty = 0;
            while (alive)
            {
                std::string launchError;
                {
                    std::lock_guard lock(outputMutex);
                    if (events.failed)
                    {
                        launchError = events.launchFailure;
                        events.armed = false;
                    }
                }
                if (!launchError.empty())
                {
                    atomicFile(jobDirectory + L"/launch-error.txt", launchError);
                    returnToMain(true);
                    return;
                }
                {
                    std::lock_guard lock(outputMutex);
                    if (stopRequested.exchange(false) && runningObserved && !events.stopSent)
                    {
                        // One-shot command; no response/prompt parsing. New events still add
                        // handles.
                        events.stopSent = true;
                        sendSteam("app_stop " + std::to_string(appId) + " 0\r");
                    }
                    if (events.uncertain)
                        throw std::runtime_error(errorText("InvalidEvent").c_str());
                    runningObserved = events.seen;
                    if (runningObserved)
                    {
                        if (phase != Phase::running)
                            setPhase(Phase::running);
                        // Allow launcher/child replacement notifications to arrive.
                        if (events.idle() && output.find("Game process ") == std::string::npos)
                        {
                            if (++empty == 6)
                            {
                                events.armed = false;
                                break;
                            }
                        }
                        else
                            empty = 0;
                    }
                    else if (std::chrono::steady_clock::now() > deadline)
                        throw std::runtime_error(errorText("NoStartEvent").c_str());
                }
                std::this_thread::sleep_for(500ms);
            }
            if (!alive)
                throw std::runtime_error(errorText("UnknownCompletion").c_str());
            waitIdle();
            atomicFile(jobDirectory + L"/game-ended", utf8(fs::path(jobDirectory).filename()));
            uploadPending = true;
        }
        setPhase(Phase::uploading);
        waitIdle();
        if (synchronize(L"finish"))
            returnToMain();
        else
            display(text("SyncPaused").c_str());
    }
    catch (const std::exception &e)
    {
        showError(e);
    }
    busy = false;
}

void rawServer()
{
    auto name = L"\\\\.\\pipe\\Kettle.CoreSession." + pipeToken + L".raw";
    for (;;)
    {
        auto h = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                                  PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT |
                                      PIPE_REJECT_REMOTE_CLIENTS,
                                  1, 65536, 65536, 0, nullptr);
        if (h == INVALID_HANDLE_VALUE)
            return;
        OVERLAPPED connection{};
        connection.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        bool connected = ConnectNamedPipe(h, &connection) != FALSE;
        if (!connected)
        {
            auto error = GetLastError();
            DWORD ignored{};
            connected =
                error == ERROR_PIPE_CONNECTED ||
                (error == ERROR_IO_PENDING && GetOverlappedResult(h, &connection, &ignored, TRUE));
        }
        CloseHandle(connection.hEvent);
        if (connected)
        {
            ULONG pid{};
            GetNamedPipeClientProcessId(h, &pid);
            frontendPid = pid;
            try
            {
                // Watch immediately, before SteamCMD starts and before REGISTER.
                watchFrontend(pid);
            }
            catch (const std::exception &e)
            {
                trace(e.what());
                DisconnectNamedPipe(h);
                CloseHandle(h);
                PostMessageW(window, WM_APP + 8, 0, 0);
                continue;
            }
            {
                std::lock_guard lock(rawMutex);
                rawClient = h;
            }
            char bytes[4096];
            DWORD count{};
            for (;;)
            {
                OVERLAPPED read{};
                read.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
                bool ok = ReadFile(h, bytes, sizeof(bytes), &count, &read) != FALSE;
                if (!ok && GetLastError() == ERROR_IO_PENDING)
                    ok = GetOverlappedResult(h, &read, &count, TRUE) != FALSE;
                CloseHandle(read.hEvent);
                if (!ok || !count)
                    break;
                if (!runtime)
                {
                    try
                    {
                        sendSteam(std::string(bytes, count));
                    }
                    catch (...)
                    {
                    }
                }
                SecureZeroMemory(bytes, sizeof(bytes));
            }
            {
                std::lock_guard lock(rawMutex);
                rawClient = INVALID_HANDLE_VALUE;
            }
        }
        DisconnectNamedPipe(h);
        CloseHandle(h);
        if (!runtime)
        {
            PostMessageW(window, WM_APP + 2, 0, 0);
        }
    }
}

std::string readLine(HANDLE h)
{
    std::string result;
    char c;
    DWORD n{};
    while (result.size() < 32768 && ReadFile(h, &c, 1, &n, nullptr) && n)
    {
        if (c == '\n')
            return result;
        result += c;
    }
    throw std::runtime_error(errorText("IncompleteRequest").c_str());
}

void controlServer()
{
    auto name = L"\\\\.\\pipe\\Kettle.CoreSession." + pipeToken + L".control";
    for (;;)
    {
        auto h = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX,
                                  PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT |
                                      PIPE_REJECT_REMOTE_CLIENTS,
                                  1, 65536, 65536, 0, nullptr);
        if (h == INVALID_HANDLE_VALUE)
            return;
        if (ConnectNamedPipe(h, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED)
        {
            try
            {
                auto command = readLine(h);
                ULONG pid{};
                GetNamedPipeClientProcessId(h, &pid);
                if (command.starts_with("REGISTER "))
                {
                    if (pid != frontendPid)
                        throw std::runtime_error(errorText("WrongFrontend").c_str());
                    auto path = fs::weakly_canonical(wide(command.substr(9)));
                    if (path.parent_path() != fs::weakly_canonical(root + L"/data/sessions") ||
                        path.filename() != pipeToken)
                        throw std::runtime_error(errorText("InvalidSessionDirectory").c_str());
                    {
                        std::lock_guard lock(stateMutex);
                        recoveryDirectory = path.wstring();
                    }
                    watchFrontend(pid);
                    writeAll(h, "OK\n", 3);
                }
                else if (command == "WATCH")
                {
                    {
                        std::lock_guard lock(stateMutex);
                        if (frontendProcess && GetProcessId(frontendProcess) != pid &&
                            WaitForSingleObject(frontendProcess, 0) != WAIT_OBJECT_0)
                            throw std::runtime_error(errorText("FrontendConnected").c_str());
                    }
                    watchFrontend(pid);
                    writeAll(h, "OK\n", 3);
                }
                else if (command == "PARK")
                {
                    std::lock_guard lock(stateMutex);
                    // A frontend may leave only while native work owns the UI. This is
                    // not DETACH: conflicts/completion must still bring Kettle back.
                    writeAll(h, frontendNeeded ? "KEEP\n" : "OK\n", frontendNeeded ? 5 : 3);
                }
                else if (command == "STATUS")
                {
                    std::lock_guard lock(stateMutex);
                    auto reply = std::to_string((int)phase.load()) + "|" + (busy ? "1" : "0") +
                                 "|" + hex(jobDirectory) + "|" + hex(currentMessage) + "|" +
                                 hex(conflictTicket) + "\n";
                    writeAll(h, reply.data(), reply.size());
                }
                else if (command.starts_with("CHOOSE "))
                {
                    std::lock_guard lock(stateMutex);
                    auto value = wide(command.substr(7));
                    auto split = value.find(L' ');
                    if (split == std::wstring::npos || conflictTicket.empty() ||
                        value.substr(split + 1) != conflictTicket || !conflictAnswer.empty())
                        throw std::runtime_error(errorText("StaleConflict").c_str());
                    auto answer = value.substr(0, split);
                    if (answer != L"local" && answer != L"cloud" && answer != L"cancel")
                        throw std::runtime_error(errorText("InvalidChoice").c_str());
                    conflictAnswer = answer;
                    frontendNeeded = answer == L"cancel";
                    conflictChanged.notify_all();
                    writeAll(h, "OK\n", 3);
                }
                else if (command == "STOP")
                {
                    if (!requestStop())
                        throw std::runtime_error(errorText("NotRunning").c_str());
                    writeAll(h, "OK\n", 3);
                }
                else if (command == "RETRY")
                {
                    if (!runtime || busy)
                        throw std::runtime_error(errorText("NotAwaitingRetry").c_str());
                    {
                        std::lock_guard lock(stateMutex);
                        frontendNeeded = false;
                    }
                    PostMessageW(window, WM_APP + 3, 1, 0);
                    writeAll(h, "OK\n", 3);
                }
                else if (command == "DETACH")
                {
                    const bool finished =
                        !busy && (phase == Phase::idle || phase == Phase::completed);
                    {
                        std::lock_guard lock(stateMutex);
                        normalClose = true;
                    }
                    if (finished)
                    {
                        // Closing a recovery window with no pending work is a real exit.
                        runtime = false;
                        writeAll(h, "OK\n", 3);
                        sendSteam("quit\r");
                    }
                    else
                    {
                        PostMessageW(window, WM_APP + 7, 0, 0);
                        writeAll(h, "OK\n", 3);
                    }
                }
                else if (command.starts_with("RUN "))
                {
                    if (runtime || busy || pid != frontendPid)
                        throw std::runtime_error(errorText("Occupied").c_str());
                    auto path = fs::weakly_canonical(wide(command.substr(4)));
                    if (path.parent_path() != fs::weakly_canonical(root + L"/data/assistant"))
                        throw std::runtime_error(errorText("InvalidJobDirectory").c_str());
                    jobDirectory = path.wstring();
                    auto file = jobDirectory + L"/job.ini";
                    auto id = ini(file, L"job", L"AppId");
                    auto numeric = std::stoull(id);
                    if (!numeric || numeric > UINT_MAX || ini(file, L"job", L"Token") != pipeToken)
                        throw std::runtime_error(errorText("InvalidJob").c_str());
                    appId = (unsigned)numeric;
                    gameName = ini(file, L"job", L"Name");
                    launchIndex = ini(file, L"job", L"LaunchIndex");
                    if (!launchIndex.empty() &&
                        launchIndex.find_first_not_of(L"0123456789") != std::wstring::npos)
                        throw std::runtime_error(errorText("InvalidLaunchOption").c_str());
                    const bool recovery = fs::exists(jobDirectory + L"/game-ended");
                    if (fs::exists(jobDirectory + L"/launch-issued") && !recovery)
                        throw std::runtime_error(errorText("UnconfirmedExit").c_str());
                    phase = recovery ? Phase::uploading : Phase::preparing;
                    uploadPending = recovery;
                    runningObserved = recovery;
                    stopping = false;
                    stopRequested = false;

                    runtime = true;
                    {
                        std::lock_guard lock(stateMutex);
                        frontendNeeded = false;
                        restarts.clear();
                        restartPaused = false;
                    }
                    atomicFile(jobDirectory + L"/native-state", "accepted");
                    writeAll(h, "OK\n", 3);
                    PostMessageW(window, WM_APP + 3, recovery ? 1 : 0, 0);
                }
                else if (command == "ATTACH")
                {
                    if ((phase != Phase::completed && phase != Phase::idle) || busy)
                        throw std::runtime_error(errorText("IncompleteRun").c_str());
                    runtime = false;
                    frontendPid = pid;
                    writeAll(h, "OK\n", 3);
                    PostMessageW(window, WM_APP + 4, 0, 0);
                }
                else if (command == "CLOSE")
                {
                    if (runtime || busy)
                        throw std::runtime_error(errorText("AssistantOwned").c_str());
                    {
                        std::lock_guard lock(stateMutex);
                        normalClose = true;
                    }
                    // Acknowledge before SteamCMD can exit and destroy the control pipe.
                    writeAll(h, "OK\n", 3);
                    sendSteam("quit\r");
                }
                else if (command == "SAFETY")
                {
                    writeAll(h, runtime || busy ? "BUSY\n" : "OK\n", runtime || busy ? 5 : 3);
                }
                else
                    throw std::runtime_error(errorText("UnknownCommand").c_str());
            }
            catch (const std::exception &e)
            {
                auto reply = std::string("ERROR ") + e.what() + "\n";
                try
                {
                    writeAll(h, reply.data(), reply.size());
                }
                catch (...)
                {
                }
            }
            FlushFileBuffers(h); // Do not discard an acknowledgement before the client reads it.
        }
        DisconnectNamedPipe(h);
        CloseHandle(h);
    }
}

void startTerminal()
{
    std::lock_guard startup(terminalMutex);
    if (abandonedLogin)
        return;
    trace("terminal start");
    HANDLE inRead{}, outWrite{};
    check(CreatePipe(&inRead, &consoleInput, nullptr, 0), errorText("InputPipe").c_str());
    check(CreatePipe(&consoleOutput, &outWrite, nullptr, 0), errorText("OutputPipe").c_str());
    auto hr = CreatePseudoConsole({4096, 80}, inRead, outWrite, 0, &console);
    CloseHandle(inRead);
    CloseHandle(outWrite);
    check(SUCCEEDED(hr), errorText("PseudoConsole").c_str());
    trace("pseudoconsole created");
    SIZE_T size{};
    InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
    std::vector<char> memory(size);
    auto attributes = (LPPROC_THREAD_ATTRIBUTE_LIST)memory.data();
    check(InitializeProcThreadAttributeList(attributes, 1, 0, &size),
          errorText("InitializeAttributes").c_str());
    check(UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, console,
                                    sizeof(console), nullptr, nullptr),
          errorText("SetPseudoConsole").c_str());
    STARTUPINFOEXW si{};
    si.StartupInfo.cb = sizeof(si);
    si.lpAttributeList = attributes;
    auto command = quote(steam) + L" -globaluser";
    PROCESS_INFORMATION pi{};
    check(CreateProcessW(steam.c_str(), command.data(), nullptr, nullptr, FALSE,
                         EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED, nullptr,
                         fs::path(steam).parent_path().c_str(), &si.StartupInfo, &pi),
          errorText("LaunchSteam").c_str());
    trace("SteamCMD created directly");
    DeleteProcThreadAttributeList(attributes);
    jobHandle = CreateJobObjectW(nullptr, nullptr);
    check(jobHandle != nullptr, errorText("CreateJob").c_str());
    // No KILL_ON_JOB_CLOSE: an assistant crash must not terminate a running game.
    check(AssignProcessToJobObject(jobHandle, pi.hProcess), errorText("AssignJob").c_str());
    steamProcess = pi.hProcess;
    ResumeThread(pi.hThread);
    CloseHandle(pi.hThread);
    trace("SteamCMD resumed");
    std::thread(
        []
        {
            Normalizer normalizer;
            char data[4096];
            DWORD length{};
            while (ReadFile(consoleOutput, data, sizeof(data), &length, nullptr) && length)
            {
                {
                    std::lock_guard lock(outputMutex);
                    normalizer.append(data, length, output);
                    size_t newline;
                    while ((newline = output.find('\n')) != std::string::npos)
                    {
                        events.line(output.substr(0, newline));
                        output.erase(0, newline + 1);
                    }
                    if (output.size() > 32768)
                    {
                        events.uncertain = true;
                        output.clear();
                    }
                }
                if (!runtime)
                {
                    std::lock_guard lock(rawMutex);
                    if (rawClient != INVALID_HANDLE_VALUE)
                    {
                        try
                        {
                            writeRaw(rawClient, data, length);
                        }
                        catch (const std::exception &e)
                        {
                            trace(e.what());
                        }
                    }
                }
                SecureZeroMemory(data, sizeof(data));
            }
            alive = false;
            PostMessageW(window, WM_APP + 5, 0, 0);
        })
        .detach();
    std::thread(
        []
        {
            WaitForSingleObject(steamProcess, INFINITE);
            // Drain output concurrently while ConPTY closes, as required by the console API.
            ClosePseudoConsole(console);
        })
        .detach();
}

// Compatibility entry point for managed ConPTY callers (startup update and the
// legacy frontend). Broker mode never invokes this: it launches SteamCMD directly.
int terminalMode(const std::vector<std::wstring> &args)
{
    auto input =
        CreateFileW(L"CONIN$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    nullptr, OPEN_EXISTING, 0, nullptr);
    auto outputHandle =
        CreateFileW(L"CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    nullptr, OPEN_EXISTING, 0, nullptr);
    check(input != INVALID_HANDLE_VALUE && outputHandle != INVALID_HANDLE_VALUE,
          errorText("ConsoleHandles").c_str());
    check(SetHandleInformation(input, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT) &&
              SetHandleInformation(outputHandle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT),
          errorText("InheritHandles").c_str());
    STARTUPINFOW si{sizeof(si)};
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput = input;
    si.hStdOutput = si.hStdError = outputHandle;
    auto command = quote(args[2]);
    for (size_t i = 3; i < args.size(); ++i)
        command += L" " + quote(args[i]);
    PROCESS_INFORMATION pi{};
    check(CreateProcessW(args[2].c_str(), command.data(), nullptr, nullptr, TRUE, 0, nullptr,
                         fs::path(args[2]).parent_path().c_str(), &si, &pi),
          errorText("ConsoleApplication").c_str());
    CloseHandle(pi.hThread);
    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code{};
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hProcess);
    CloseHandle(input);
    CloseHandle(outputHandle);
    return (int)code;
}

void artwork()
{
    auto path = root + L"/data/images/headers/" + std::to_wstring(appId) + L".img";
    Gdiplus::Bitmap image(path.c_str());
    HBITMAP bitmap{};
    if (image.GetLastStatus() == Gdiplus::Ok &&
        image.GetHBITMAP(Gdiplus::Color(240, 240, 240), &bitmap) == Gdiplus::Ok)
    {
        auto control = GetDlgItem(window, 100);
        auto previous = (HBITMAP)SendMessageW(control, STM_SETIMAGE, IMAGE_BITMAP, (LPARAM)bitmap);
        if (previous)
            DeleteObject(previous);
    }
}

LRESULT CALLBACK windowProc(HWND hwnd, UINT message, WPARAM w, LPARAM l)
{
    switch (message)
    {
    case WM_APP + 1:
    {
        auto text = (std::wstring *)l;
        SetWindowTextW(statusLabel, text->c_str());
        delete text;
        return 0;
    }
    case WM_APP + 2:
        return 0;
    case WM_APP + 3:
        ShowWindow(hwnd, SW_SHOW);
        SetForegroundWindow(hwnd);
        artwork();
        std::thread([retry = w != 0] { runGame(retry); }).detach();
        return 0;
    case WM_APP + 4:
        ShowWindow(hwnd, SW_HIDE);
        return 0;
    case WM_APP + 5:
        if (!runtime)
            DestroyWindow(hwnd);
        else
            display(text("SteamExited").c_str());
        return 0;
    case WM_APP + 7:
        ShowWindow(hwnd, SW_SHOW);
        return 0;
    case WM_APP + 8:
    {
        {
            std::lock_guard lock(stateMutex);
            if (runtime || busy || !recoveryDirectory.empty())
                return 0;
            normalClose = true;
            abandonedLogin = true;
        }
        // During login SteamCMD may be waiting for password/Guard input, so sending
        // "quit" is not a reliable shutdown. This job contains only our pre-game login.
        std::lock_guard startup(terminalMutex);
        if (jobHandle && !TerminateJobObject(jobHandle, 1))
        {
            trace("failed to terminate abandoned login job");
            ShowWindow(hwnd, SW_SHOW);
            SetWindowTextW(statusLabel, text("LoginStopFailed").c_str());
            return 0;
        }
        DestroyWindow(hwnd);
        return 0;
    }
    case WM_TIMER:
        EnableWindow(stopButton, phase == Phase::running && busy && !uploadPending && !stopping);
        return 0;
    case WM_APP + 9:
        if (stopping)
            EnableWindow(stopButton, FALSE);
        return 0;
    case WM_COMMAND:
        if (LOWORD(w) == 101 && requestStop())
            EnableWindow(stopButton, FALSE);
        return 0;
    case WM_CLOSE:
        if (!steamProcess)
        {
            DestroyWindow(hwnd);
            return 0;
        }
        if (runtime && !busy && phase == Phase::preparing)
        {
            if (MessageBoxW(hwnd, text("CancelLaunch").c_str(), L"Kettle",
                            MB_YESNO | MB_ICONQUESTION) == IDYES)
            {
                try
                {
                    setPhase(Phase::completed);
                    busy = false;
                    std::lock_guard lock(stateMutex);
                    if (!recoveryDirectory.empty())
                    {
                        frontendNeeded = true;
                        normalClose = false;
                        restartPaused = false;
                        restarts.clear();
                    }
                    else
                    {
                        auto process =
                            launch(root + L"/Kettle.exe",
                                   L"--core-session --core-session-resume " + quote(jobDirectory),
                                   root, 0);
                        CloseHandle(process);
                    }
                }
                catch (const std::exception &e)
                {
                    showError(e);
                }
            }
            return 0;
        }
        if (runtime || busy)
        {
            MessageBoxW(hwnd, text("SessionActive").c_str(), L"Kettle", MB_OK);
            return 0;
        }
        {
            std::lock_guard lock(stateMutex);
            normalClose = true;
        }
        try
        {
            sendSteam("quit\r");
        }
        catch (...)
        {
        }
        ShowWindow(hwnd, SW_HIDE);
        return 0;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, message, w, l);
}

int selfTest()
{
    auto english = localization::read(L"en");
    auto chinese = localization::read(L"zh-CN");
    check(english.size() == chinese.size(), "Language resource key counts differ");
    for (auto &[key, value] : english)
        check(chinese.count(key) == 1, "Missing Chinese translation");
    localization::initialize(L"zh-CN");
    check(text("Stop") == L"停止", "Embedded Chinese resource failed");
    check(errorText("CloudFailure") == utf8(text("CloudFailure")), "UTF-8 error text failed");
    localization::initialize(L"unsupported");
    check(text("Stop") == L"Stop", "English language fallback failed");
    Normalizer n;
    std::string text;
    n.append("\x1b[3", 4, text);
    n.append("1mabc\r", 6, text);
    n.append("\ndef\bG", 7, text);
    check(text == "abc\ndeG", "Normalizer failed");
    GameEvents test;
    test.armed = true;
    test.target = 123;
    test.line("Steam>Game process added : AppID 123 \"game.exe\", ProcID 42, IP 0:0");
    test.line("Game process updated : AppID 123 \"game.exe\", ProcID 42, IP 0:0");
    check(test.seen && test.active.size() == 1, "Start/update event failed");
    test.line("Game process added : AppID 123 \"child.exe\", ProcID 43");
    test.line("Game process removed : AppID 123 \"game.exe\", ProcID 42");
    check(test.active.size() == 1, "Parent exit lost child");
    test.line("Game process removed : AppID 123 \"child.exe\", ProcID 43");
    check(test.active.empty() && !test.uncertain, "Exit event failed");
    test.line("Game process removed : AppID 123 \"unknown.exe\", ProcID 44");
    check(test.uncertain, "Unknown removal accepted");
    GameEvents auxiliary;
    auxiliary.armed = true;
    auxiliary.target = 123;
    auxiliary.line("Game process added : AppID 123 \"Kettle.CloudHost.exe\", ProcID 50");
    check(!auxiliary.seen && auxiliary.active.empty(), "Cloud helper counted as game");
    auxiliary.line("Game process removed : AppID 123");
    check(auxiliary.uncertain, "Truncated event accepted");
    GameEvents other;
    other.armed = true;
    other.target = 123;
    other.line("Game process added : AppID 456 \"other.exe\", ProcID 60");
    other.line("Game process removed : AppID 456 \"other.exe\", ProcID 60");
    check(!other.seen && other.active.empty(), "Unrelated game confirmed launch");
    other.line("AppId 123 launch failed (error)");
    check(other.failed, "Launch failure ignored");
    GameEvents overflow;
    overflow.line("Game process added : AppID 123 \"game.exe\", ProcID 99999999999999999999999");
    check(overflow.uncertain, "Oversized PID accepted");
    GameEvents wrapped;
    wrapped.line("Game process removed : AppID 123 \"" + std::string(4096, 'x') + "\", ProcID 1");
    check(wrapped.uncertain, "Wrapped PID prefix accepted");
    GameEvents stopped;
    stopped.begin(123);
    stopped.stopSent = true;
    check(!stopped.processesExited({}), "No reported PID confirmed exit");
    stopped.line("Game process added : AppID 123 \"game.exe\", ProcID 42");
    stopped.line("Game process updated : AppID 123 \"child.exe\", ProcID 43");
    stopped.line("Game process removed : AppID 123 \"child.exe\", ProcID 43");
    check(!stopped.processesExited({43}), "Removed event overrode an existing PID");
    check(stopped.processesExited({99}), "Missing removal prevented PID-based completion");
    stopped.line("Game process updated : AppID 123 \"late.exe\", ProcID 44");
    check(!stopped.processesExited({44}), "New PID after Stop was ignored");
    GameEvents current;
    current.begin(123);
    current.stopSent = true;
    current.reportedPids.insert(GetCurrentProcessId());
    check(!current.idle(), "Live process missing from system enumeration");
    GameEvents helper;
    check(helper.helperExited(4424, false), "No-Steam worker unexpectedly needs an event");
    check(!helper.helperExited(4424, true), "OS exit substituted for Steam removal");
    helper.line("Game process added : AppID 736190 \"Kettle.CloudHost.exe --sync job prepare none "
                "-\", ProcID 4424, IP 0.0.0.0:0");
    check(!helper.helperExited(4424, true), "Helper still registered");
    helper.line("Game process removed: AppID 736190 \"Kettle.CloudHost.exe --sync job prepare none "
                "-\", ProcID 4424 ");
    check(helper.helperExited(4424, true) && !helper.helperExited(4425, true),
          "Helper exit identity mismatch");
    helper.begin(736190);
    helper.line("ERROR! AppId 736190 launch failed (Application running)");
    check(helper.failed && helper.launchFailure.find("Application running") != std::string::npos,
          "Real launch failure lost its reason");
    check(quote(L"a\\") == L"\"a\\\\\"", "Argument quoting failed");
    return 0;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    try
    {
        localization::initialize(L"en");
        int argc{};
        auto argv = CommandLineToArgvW(GetCommandLineW(), &argc);
        std::vector<std::wstring> args;
        for (int i = 0; i < argc; ++i)
            args.emplace_back(argv[i]);
        LocalFree(argv);
        if (args.size() >= 3 && args[1] == L"--terminal")
            return terminalMode(args);
        // The trailing option keeps the original broker invocation compatible.
        if (args.size() >= 4 && args[args.size() - 2] == L"--language")
        {
            localization::initialize(args.back());
            args.resize(args.size() - 2);
        }
        if (args.size() == 2 && args[1] == L"--self-test")
            return selfTest();
        bool preview = args.size() == 2 && args[1] == L"--preview";
        if (!preview && (args.size() != 5 || args[1] != L"--broker"))
            return 2;
        wchar_t path[32768];
        GetModuleFileNameW(nullptr, path, 32768);
        self = path;
        root = fs::path(self).parent_path();
        if (!preview)
        {
            pipeToken = args[2];
            steam = args[3];
            root = args[4];
        }
        Gdiplus::GdiplusStartupInput gdiplusInput;
        ULONG_PTR gdiplusToken;
        Gdiplus::GdiplusStartup(&gdiplusToken, &gdiplusInput, nullptr);
        WNDCLASSW wc{};
        wc.hInstance = instance;
        wc.lpfnWndProc = windowProc;
        wc.lpszClassName = L"KettleCoreSession";
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
        RegisterClassW(&wc);
        window =
            CreateWindowW(wc.lpszClassName, text("WindowTitle").c_str(),
                          WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX, CW_USEDEFAULT,
                          CW_USEDEFAULT, 510, 410, nullptr, nullptr, instance, nullptr);
        CreateWindowW(L"STATIC", L"", WS_CHILD | WS_VISIBLE | SS_BITMAP | SS_CENTERIMAGE, 15, 12,
                      460, 180, window, (HMENU)100, instance, nullptr);
        statusLabel = CreateWindowW(L"STATIC", text("Waiting").c_str(), WS_CHILD | WS_VISIBLE, 15,
                                    200, 460, 100, window, nullptr, instance, nullptr);
        stopButton = CreateWindowW(L"BUTTON", text("Stop").c_str(),
                                   WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_DISABLED, 370, 320, 105,
                                   30, window, (HMENU)101, instance, nullptr);
        for (auto control : {statusLabel, stopButton})
            SendMessageW(control, WM_SETFONT, (WPARAM)GetStockObject(DEFAULT_GUI_FONT), TRUE);
        SetTimer(window, 1, 500, nullptr);
        if (preview)
        {
            ShowWindow(window, SW_SHOW);
            SetWindowTextW(statusLabel, text("Preview").c_str());
        }
        else
        {
            std::thread(rawServer).detach();
            std::thread(controlServer).detach();
            std::thread(superviseFrontend).detach();
            // Wait for the frontend before starting SteamCMD, so login prompts cannot be lost.
            std::thread(
                []
                {
                    try
                    {
                        for (int i = 0; i < 300; ++i)
                        {
                            bool connected;
                            {
                                std::lock_guard lock(rawMutex);
                                connected = rawClient != INVALID_HANDLE_VALUE;
                            }
                            if (connected)
                            {
                                startTerminal();
                                return;
                            }
                            std::this_thread::sleep_for(100ms);
                        }
                        trace("frontend connect timeout");
                        PostMessageW(window, WM_CLOSE, 0, 0);
                    }
                    catch (const std::exception &e)
                    {
                        trace(e.what());
                        showError(e);
                    }
                })
                .detach();
        }
        MSG msg;
        while (GetMessageW(&msg, nullptr, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        return 0;
    }
    catch (const std::exception &e)
    {
        MessageBoxW(nullptr, wide(e.what()).c_str(),
                    localization::strings.empty() ? L"Kettle CoreSession"
                                                  : text("WindowTitle").c_str(),
                    MB_OK | MB_ICONERROR);
        return 1;
    }
}

// The broker is launched with CREATE_NO_WINDOW. SteamCMD is attached directly
// to its ConPTY; no second CoreSession or managed terminal wrapper is launched.
int wmain()
{
    return wWinMain(GetModuleHandleW(nullptr), nullptr, nullptr, 0);
}
