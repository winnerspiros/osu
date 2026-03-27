// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#pragma once

#include <vulkan/vulkan.h>
#include <string>
#include <cstdint>

/// Lightweight Vulkan capability probe for Android.
/// Requires Vulkan 1.3 as minimum for full feature detection (dynamic rendering,
/// synchronization2). Falls back gracefully on older devices.
/// Does NOT create a full rendering pipeline — it checks device support
/// and reports GPU capabilities so the game can make informed decisions
/// about rendering strategy and low-latency presentation.
class VulkanProbe {
public:
    struct DeviceInfo {
        std::string deviceName;
        uint32_t apiVersion = 0;
        uint32_t driverVersion = 0;
        uint32_t vendorId = 0;
        bool supportsSwapchain = false;
        /// Total device-local memory in megabytes.
        uint32_t deviceLocalMemoryMB = 0;
        /// Number of queue families available.
        uint32_t queueFamilyCount = 0;
        /// Whether the device has a dedicated compute queue (separate from graphics).
        bool hasDedicatedComputeQueue = false;
        /// Whether the device has a dedicated transfer queue.
        bool hasDedicatedTransferQueue = false;
        /// Whether the device likely supports VK_PRESENT_MODE_MAILBOX_KHR for low-latency rendering.
        /// Detected via the VK_GOOGLE_display_timing device extension, which is present on
        /// Android GPUs (Adreno, Mali) that also expose MAILBOX present mode.
        bool supportsMailboxPresentMode = false;
        /// Whether the device reports Vulkan 1.3+ API version.
        bool meetsVulkan13 = false;
        /// Whether VkPhysicalDeviceVulkan13Features::dynamicRendering is supported.
        /// Dynamic rendering eliminates VkRenderPass/VkFramebuffer boilerplate.
        bool supportsDynamicRendering = false;
        /// Whether VkPhysicalDeviceVulkan13Features::synchronization2 is supported.
        /// Provides a cleaner, less error-prone synchronization model.
        bool supportsSynchronization2 = false;
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
    void queryMemory(VkPhysicalDevice device);
    void queryQueueFamilies(VkPhysicalDevice device);
    void queryMailboxSupport(VkPhysicalDevice device);
    void queryVulkan13Features(VkPhysicalDevice device);
    void cleanup();
};
