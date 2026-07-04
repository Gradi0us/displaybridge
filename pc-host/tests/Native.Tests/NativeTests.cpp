// Native.Tests — M0.1 scaffold. Minimal GoogleTest case proving the
// test toolchain links against DisplayBridge.Native and can call into it.

#include "gtest/gtest.h"

extern "C" __declspec(dllimport) int DisplayBridge_GetVersion();

TEST(DisplayBridgeNativeTest, GetVersionReturnsOne)
{
    EXPECT_EQ(DisplayBridge_GetVersion(), 1);
}
