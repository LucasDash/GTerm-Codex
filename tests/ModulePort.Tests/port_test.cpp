#include "module/source/PortConfig.h"
#include <cstdint>
#include <cstdio>

int main()
{
    struct Case { const char* value; bool valid; unsigned int port; };
    const Case cases[] = {
        {nullptr, false, 0}, {"", false, 0}, {"1", true, 1},
        {"27524", true, 27524}, {"65535", true, 65535},
        {"0", false, 0}, {"65536", false, 0},
        {"-1", false, 0}, {"80x", false, 0},
        {"999999999999999999999", false, 0}
    };
    for (const auto& test : cases)
    {
        std::uint16_t result = 222;
        const bool ok = ParseGTermModulePort(test.value, result);
        if (ok != test.valid || (ok && result != test.port) || (!ok && result != 222))
            return 1;
    }
    std::puts("Native port parser cases passed");
    return 0;
}
