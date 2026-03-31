// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#pragma once

#include <vulkan/vulkan.h>
#include <cstdint>
#include <string>

typedef uint8_t byte;

/// Lightweight Vulkan capability probe for Android.
/// Requires Vulkan 1.3 as minimum for full feature detection (dynamic rendering,
/// synchronization2). Falls back gracefully on older devices.
class VulkanProbe {
public:
    struct DeviceInfo {
        std::string deviceName;
        uint32_t apiVersion = 0;
        uint32_t driverVersion = 0;
        uint32_t vendorId = 0;
        bool supportsSwapchain = false;
        uint32_t deviceLocalMemoryMB = 0;
        uint32_t queueFamilyCount = 0;
        bool hasDedicatedComputeQueue = false;
        bool hasDedicatedTransferQueue = false;
        bool supportsMailboxPresentMode = false;
        bool meetsVulkan13 = false;
        bool supportsDynamicRendering = false;
        bool supportsSynchronization2 = false;

        // API 31+ / Modern High-Performance Extensions
        bool supportsPresentId = false;
        bool supportsPresentWait = false;
        bool supportsGraphicsPipelineLibrary = false;
        bool supportsShaderObject = false;
        bool supportsGlobalPriority = false;
        bool supportsMemoryBudget = false;
    };

    VulkanProbe();
    ~VulkanProbe();

    bool isAvailable() const { return available_; }
    const DeviceInfo& getDeviceInfo() const { return deviceInfo_; }

private:
    bool available_ = false;
    DeviceInfo deviceInfo_{};
    VkInstance instance_ = VK_NULL_HANDLE;

    bool createInstance();
    bool queryDevice();
    void queryMemory(VkPhysicalDevice device);
    void queryQueueFamilies(VkPhysicalDevice device);
    void queryMailboxSupport(VkPhysicalDevice device);
    void queryVulkan13Features(VkPhysicalDevice device);
    void queryModernExtensions(VkPhysicalDevice device);
    void cleanup();
};
