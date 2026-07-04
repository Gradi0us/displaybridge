// DisplayBridge.Native — M0.1 scaffold.
// Purpose: prove the C++ toolchain builds inside the solution. Real
// Media Foundation HEVC encoder / IddCx / input injection logic is
// deferred to M1/M2 — do NOT add business logic here yet.

extern "C" __declspec(dllexport) int DisplayBridge_GetVersion()
{
    return 1;
}
