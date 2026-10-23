#pragma once
#include <map>
#include <sstream>

// UTF-8 key=value files are RCDATA resources; no translation files are needed at runtime.
namespace localization
{
using Catalog = std::map<std::string, std::wstring>;
inline Catalog strings;
inline std::map<std::string, std::string> utf8Strings;

inline Catalog read(const wchar_t *name)
{
    auto resource = FindResourceW(
        nullptr, MAKEINTRESOURCEW(wcscmp(name, L"zh-CN") == 0 ? 202 : 201), RT_RCDATA);
    if (!resource)
        throw std::runtime_error("Missing CoreSession language resource.");
    auto data = LoadResource(nullptr, resource);
    auto bytes = static_cast<const char *>(LockResource(data));
    auto size = SizeofResource(nullptr, resource);
    if (!bytes || !size)
        throw std::runtime_error("Missing CoreSession language resource.");
    Catalog result;
    std::istringstream input(std::string(bytes, size));
    std::string line;
    while (std::getline(input, line))
    {
        if (!line.empty() && line.back() == '\r')
            line.pop_back();
        if (line.empty() || line[0] == '#' || line[0] == '[')
            continue;
        auto separator = line.find('=');
        if (separator == std::string::npos || separator == 0)
            throw std::runtime_error("Invalid CoreSession language entry.");
        auto key = line.substr(0, separator);
        auto value = wide(line.substr(separator + 1));
        if (value.empty() || !result.emplace(key, value).second)
            throw std::runtime_error("Empty or duplicate CoreSession language entry.");
    }
    return result;
}

inline void initialize(const std::wstring &language)
{
    strings = read(L"en");
    if (_wcsicmp(language.c_str(), L"zh-CN") == 0)
        for (auto &[key, value] : read(L"zh-CN"))
            strings[key] = value;
    utf8Strings.clear();
    for (auto &[key, value] : strings)
        utf8Strings[key] = utf8(value);
}
} // namespace localization

inline const std::wstring &text(const char *key)
{
    return localization::strings.at(key);
}

inline const std::string &errorText(const char *key)
{
    return localization::utf8Strings.at(key);
}
