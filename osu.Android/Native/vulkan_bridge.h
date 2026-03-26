// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#pragma once

#include <vulkan/vulkan.h>
#include <string>

/// Lightweight Vulkan capability probe for Android.
/// Does NOT create a full rendering pipeline - it checks device support
/// and reports GPU capabilities so the game can make informed decisions.
class VulkanProbe {
public:
    struct DeviceInfo {
        std::string deviceName;
        uint32_t apiVersion;
        uint32_t driverVersion;
        uint32_t vendorId;
        bool supportsSwapchain;
    };

    VulkanProbe();
    ~VulkanProbe();

    /// Returns true if Vulkan is available on this device.
    bool isAvailable() const { return available_; }

    /// Returns info about the selected physical device.
    const DeviceInfo& getDeviceInfo() const { return deviceInfo_; }

private:
    bool available_ = false;
    DeviceInfo deviceInfo_{};
    VkInstance instance_ = VK_NULL_HANDLE;

    bool createInstance();
    bool queryDevice();
    void cleanup();
};
