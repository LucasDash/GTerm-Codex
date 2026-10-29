#pragma once

#include <cstdint>

// Empty/unset is handled by the caller. Accept decimal ports only.
inline bool ParseGTermModulePort(const char* text, std::uint16_t& result)
{
    if (!text || !*text) return false;
    unsigned int value = 0;
    for (const char* p = text; *p; ++p)
    {
        if (*p < '0' || *p > '9') return false;
        value = value * 10 + static_cast<unsigned int>(*p - '0');
        if (value > 65535) return false;
    }
    if (value == 0) return false;
    result = static_cast<std::uint16_t>(value);
    return true;
}
