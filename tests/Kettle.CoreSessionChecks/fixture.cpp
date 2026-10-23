#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <iostream>
#include <fstream>
#include <filesystem>
#include <thread>
#include <chrono>

DWORD gameProcess(DWORD duration) {
    wchar_t path[32768];
    GetModuleFileNameW(nullptr, path, 32768);
    auto command = L"\"" + std::wstring(path) + L"\" --game " + std::to_wstring(duration);
    STARTUPINFOW startup{sizeof(startup)};
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(path, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
                        nullptr, nullptr, &startup, &process))
        ExitProcess(92);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return process.dwProcessId;
}

// Offline fixture only; never connects to Steam or starts a real game.
int wmain(int argc, wchar_t** argv) {
    if (argc == 3 && std::wstring(argv[1]) == L"--game") {
        Sleep(std::stoul(argv[2]));
        return 0;
    }
    wchar_t mode[8];
    bool stopCheck = GetEnvironmentVariableW(L"KETTLE_FIXTURE_STOP", mode, 8) != 0;
    DWORD child = 0;
    int stops = 0;
    if (argc == 3 && std::wstring(argv[1]) == L"--core-session-resume") {
        std::ofstream(std::filesystem::path(argv[2]) / "restarts", std::ios::app) << "restart\n";
        return 7; // Repeated frontend crashes must not restart the game or loop indefinitely.
    }
    if (argc == 4 && std::wstring(argv[2]) == L"--core-session-resume") {
        std::ofstream(std::filesystem::path(argv[3]) / "returned-to-main") << "ok"; return 0;
    }
    std::cout << "Steam>" << std::flush;
    std::string line;
    bool waitingForLogin = false;
    while (std::getline(std::cin, line)) {
        while (!line.empty() && (line.back() == '\r' || line.back() == '\n')) line.pop_back();
        if (line == "login-crash-fixture") {
            waitingForLogin = true;
            std::cout << "login-waiting-pid=" << GetCurrentProcessId() << ";\n" << std::flush;
            continue;
        }
        if (waitingForLogin) continue; // Simulate a password/Guard prompt that ignores quit.
        if (line == "quit") return 0;
        if (line == "apps_running") return 91; // Native monitoring must never poll.
        if (line.starts_with("app_stop ") && stopCheck) {
            if (++stops != 1) return 93;
            // removed is deliberately premature; this PID remains alive for several seconds.
            std::cout << "Game process removed : AppID 123 \"child.exe\", ProcID " << child << "\n";
            // A newly reported PID after Stop must also delay synchronization.
            auto late = gameProcess(10000);
            std::cout << "Game process updated : AppID 123 \"late.exe\", ProcID " << late << "\n" << std::flush;
            continue; // No command acknowledgement and no later removed events.
        }
        if (line.starts_with("app_launch ")) {
            if (GetEnvironmentVariableW(L"KETTLE_FIXTURE_LAUNCH_FAIL", mode, 8)) {
                std::cout << "ERROR! AppId 123 launch failed (Application running)\nSteam>" << std::flush;
                continue;
            }
            if (stopCheck) {
                auto parent = gameProcess(6000);
                child = gameProcess(8000);
                std::cout << "Game process added : AppID 123 \"parent.exe\", ProcID " << parent << "\n"
                          << "Game process updated : AppID 123 \"child.exe\", ProcID " << child << "\n" << std::flush;
                continue;
            }
            std::thread([] {
                // Events can interrupt a prompt and span pipe reads. No final prompt on exit.
                std::cout << "Steam>Game process ad" << std::flush;
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
                std::cout << "ded : AppID 123 \"fixture.exe\", ProcID 2147483000\n" << std::flush;
                std::this_thread::sleep_for(std::chrono::seconds(4));
                std::cout << "Game process added : AppID 123 \"child.exe\", ProcID 2147483001\n"
                             "Game process removed : AppID 123 \"fixture.exe\", ProcID 2147483000\n" << std::flush;
                std::this_thread::sleep_for(std::chrono::seconds(2));
                std::cout << "Game process removed : AppID 123 \"child.exe\", ProcID 2147483001\n" << std::flush;
            }).detach();
            continue;
        }
        // Ordinary commands still use a prompt; game exit events do not.
        std::cout << "Steam>" << std::flush;
    }
}
