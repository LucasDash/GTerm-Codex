# Native module port parser checks

From an MSVC Developer Command Prompt at the repository root:

```cmd
cl /nologo /EHsc /I . tests\ModulePort.Tests\port_test.cpp
port_test.exe
```

This checks port boundaries and rejects malformed input. A passing run does
not prove that a compiled GMod binary loads or binds its socket.
