// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#include "vulkan_bridge.h"
#include <android/log.h>
#include <cstdint>
#include <cstring>
#include <set>
#include <vector>

#define LOG_TAG "osu!native"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

VulkanProbe::VulkanProbe() {
    available_ = createInstance() && queryDevice();

    if (!available_) {
        cleanup();
    }

    if (available_) {
        LOGI("Vulkan available: %s (API %u.%u.%u, driver %u, VRAM %u MB, "
             "queues %u, dedicatedCompute=%d, dedicatedTransfer=%d, swapchain=%d, mailbox=%d)",
             deviceInfo_.deviceName.c_str(),
             VK_VERSION_MAJOR(deviceInfo_.apiVersion),
             VK_VERSION_MINOR(deviceInfo_.apiVersion),
             VK_VERSION_PATCH(deviceInfo_.apiVersion),
             deviceInfo_.driverVersion,
             deviceInfo_.deviceLocalMemoryMB,
             deviceInfo_.queueFamilyCount,
             deviceInfo_.hasDedicatedComputeQueue ? 1 : 0,
             deviceInfo_.hasDedicatedTransferQueue ? 1 : 0,
             deviceInfo_.supportsSwapchain ? 1 : 0,
             deviceInfo_.supportsMailboxPresentMode ? 1 : 0);
    } else {
        LOGI("Vulkan not available on this device");
    }
}

VulkanProbe::~VulkanProbe() {
    cleanup();
}

bool VulkanProbe::createInstance() {
    VkApplicationInfo appInfo{};
    appInfo.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    appInfo.pApplicationName = "osu!";
    appInfo.applicationVersion = VK_MAKE_VERSION(1, 0, 0);
    appInfo.pEngineName = "osu-framework";
    appInfo.engineVersion = VK_MAKE_VERSION(1, 0, 0);
    appInfo.apiVersion = VK_API_VERSION_1_0;

    VkInstanceCreateInfo createInfo{};
    createInfo.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    createInfo.pApplicationInfo = &appInfo;
    createInfo.enabledLayerCount = 0;
    createInfo.enabledExtensionCount = 0;

    VkResult result = vkCreateInstance(&createInfo, nullptr, &instance_);

    if (result != VK_SUCCESS) {
        LOGE("vkCreateInstance failed: %d", result);
        return false;
    }

    return true;
}

bool VulkanProbe::queryDevice() {
    uint32_t deviceCount = 0;
    vkEnumeratePhysicalDevices(instance_, &deviceCount, nullptr);

    if (deviceCount == 0) {
        LOGI("No Vulkan physical devices found");
        return false;
    }

    std::vector<VkPhysicalDevice> devices(deviceCount);

    if (vkEnumeratePhysicalDevices(instance_, &deviceCount, devices.data()) != VK_SUCCESS || deviceCount == 0) {
        LOGE("Failed to enumerate Vulkan physical devices");
        return false;
    }

    // Pick the first discrete GPU, or fall back to the first device.
    VkPhysicalDevice selected = devices[0];

    for (const auto& dev : devices) {
        VkPhysicalDeviceProperties props;
        vkGetPhysicalDeviceProperties(dev, &props);

        if (props.deviceType == VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU) {
            selected = dev;
            break;
        }
    }

    VkPhysicalDeviceProperties props;
    vkGetPhysicalDeviceProperties(selected, &props);

    deviceInfo_.deviceName = props.deviceName;
    deviceInfo_.apiVersion = props.apiVersion;
    deviceInfo_.driverVersion = props.driverVersion;
    deviceInfo_.vendorId = props.vendorID;

    // Check for swapchain extension support.
    uint32_t extCount = 0;

    if (vkEnumerateDeviceExtensionProperties(selected, nullptr, &extCount, nullptr) != VK_SUCCESS || extCount == 0) {
        deviceInfo_.supportsSwapchain = false;
    } else {
        std::vector<VkExtensionProperties> extensions(extCount);

        if (vkEnumerateDeviceExtensionProperties(selected, nullptr, &extCount, extensions.data()) != VK_SUCCESS) {
            deviceInfo_.supportsSwapchain = false;
        } else {
            deviceInfo_.supportsSwapchain = false;

            for (const auto& ext : extensions) {
                if (strcmp(ext.extensionName, VK_KHR_SWAPCHAIN_EXTENSION_NAME) == 0) {
                    deviceInfo_.supportsSwapchain = true;
                    break;
                }
            }
        }
    }

    // Query additional performance-relevant capabilities.
    queryMemory(selected);
    queryQueueFamilies(selected);
    queryMailboxSupport(selected);

    return true;
}

void VulkanProbe::queryMemory(VkPhysicalDevice device) {
    VkPhysicalDeviceMemoryProperties memProps;
    vkGetPhysicalDeviceMemoryProperties(device, &memProps);

    uint64_t deviceLocalBytes = 0;

    for (uint32_t i = 0; i < memProps.memoryHeapCount; i++) {
        if (memProps.memoryHeaps[i].flags & VK_MEMORY_HEAP_DEVICE_LOCAL_BIT) {
            deviceLocalBytes += memProps.memoryHeaps[i].size;
        }
    }

    deviceInfo_.deviceLocalMemoryMB = static_cast<uint32_t>(deviceLocalBytes / (1024 * 1024));
}

void VulkanProbe::queryQueueFamilies(VkPhysicalDevice device) {
    uint32_t queueFamilyCount = 0;
    vkGetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, nullptr);

    deviceInfo_.queueFamilyCount = queueFamilyCount;
    deviceInfo_.hasDedicatedComputeQueue = false;
    deviceInfo_.hasDedicatedTransferQueue = false;

    if (queueFamilyCount == 0) return;

    std::vector<VkQueueFamilyProperties> families(queueFamilyCount);
    vkGetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, families.data());

    for (const auto& family : families) {
        // A dedicated compute queue has compute but NOT graphics.
        bool hasGraphics = (family.queueFlags & VK_QUEUE_GRAPHICS_BIT) != 0;
        bool hasCompute = (family.queueFlags & VK_QUEUE_COMPUTE_BIT) != 0;
        bool hasTransfer = (family.queueFlags & VK_QUEUE_TRANSFER_BIT) != 0;

        if (hasCompute && !hasGraphics) {
            deviceInfo_.hasDedicatedComputeQueue = true;
        }

        if (hasTransfer && !hasGraphics && !hasCompute) {
            deviceInfo_.hasDedicatedTransferQueue = true;
        }
    }
}

void VulkanProbe::queryMailboxSupport(VkPhysicalDevice device) {
    // MAILBOX present mode requires a VkSurface to query definitively, but we detect
    // it using VK_GOOGLE_display_timing — a device extension that is present exclusively
    // on Android GPUs (Adreno, Mali) that also expose MAILBOX present mode support.
    // This gives us a reliable indication without needing an active surface.
    deviceInfo_.supportsMailboxPresentMode = false;

    uint32_t extCount = 0;

    if (vkEnumerateDeviceExtensionProperties(device, nullptr, &extCount, nullptr) != VK_SUCCESS || extCount == 0)
        return;

    std::vector<VkExtensionProperties> extensions(extCount);

    if (vkEnumerateDeviceExtensionProperties(device, nullptr, &extCount, extensions.data()) != VK_SUCCESS)
        return;

    for (const auto& ext : extensions) {
        if (strcmp(ext.extensionName, "VK_GOOGLE_display_timing") == 0) {
            deviceInfo_.supportsMailboxPresentMode = true;
            return;
        }
    }
}

void VulkanProbe::cleanup() {
    if (instance_ != VK_NULL_HANDLE) {
        vkDestroyInstance(instance_, nullptr);
        instance_ = VK_NULL_HANDLE;
    }
}

// ============================================================
// C exports for P/Invoke from .NET
// ============================================================

// Use intptr_t for pointer handles so the size matches C# IntPtr on both
// 32-bit (4 bytes) and 64-bit (8 bytes) platforms.  The previous use of
// C++ `long` was 4 bytes on 32-bit ARM/x86 but C# `long` is always
// 8 bytes, causing a calling-convention mismatch and crash.

#define OSU_EXPORT __attribute__((visibility("default")))

extern "C" {

OSU_EXPORT intptr_t nVulkanProbeCreate() {
    auto* probe = new (std::nothrow) VulkanProbe();
    return reinterpret_cast<intptr_t>(probe);
}

OSU_EXPORT void nVulkanProbeDestroy(intptr_t ptr) {
    if (ptr) delete reinterpret_cast<VulkanProbe*>(ptr);
}

OSU_EXPORT unsigned char nVulkanIsAvailable(intptr_t ptr) {
    auto* probe = reinterpret_cast<VulkanProbe*>(ptr);
    return (probe && probe->isAvailable()) ? 1 : 0;
}

OSU_EXPORT int nVulkanGetApiVersion(intptr_t ptr) {
    auto* probe = reinterpret_cast<VulkanProbe*>(ptr);
    return probe ? static_cast<int>(probe->getDeviceInfo().apiVersion) : 0;
}

OSU_EXPORT unsigned char nVulkanSupportsSwapchain(intptr_t ptr) {
    auto* probe = reinterpret_cast<VulkanProbe*>(ptr);
    return (probe && probe->getDeviceInfo().supportsSwapchain) ? 1 : 0;
}

OSU_EXPORT int nVulkanGetDeviceLocalMemoryMB(intptr_t ptr) {
    auto* probe = reinterpret_cast<VulkanProbe*>(ptr);
    return probe ? static_cast<int>(probe->getDeviceInfo().deviceLocalMemoryMB) : 0;
}

OSU_EXPORT int nVulkanGetQueueFamilyCount(intptr_t ptr) {
    auto* probe = reinterpret_cast<VulkanProbe*>(ptr);
    return probe ? static_cast<int>(probe->getDeviceInfo().queueFamilyCount) : 0;
}

OSU_EXPORT unsigned char nVulkanHasDedicatedComputeQueue(intptr_t ptr) {
    auto* probe = reinterpret_cast<VulkanProbe*>(ptr);
    return (probe && probe->getDeviceInfo().hasDedicatedComputeQueue) ? 1 : 0;
}

OSU_EXPORT unsigned char nVulkanHasDedicatedTransferQueue(intptr_t ptr) {
    auto* probe = reinterpret_cast<VulkanProbe*>(ptr);
    return (probe && probe->getDeviceInfo().hasDedicatedTransferQueue) ? 1 : 0;
}

OSU_EXPORT unsigned char nVulkanSupportsMailboxPresentMode(intptr_t ptr) {
    auto* probe = reinterpret_cast<VulkanProbe*>(ptr);
    return (probe && probe->getDeviceInfo().supportsMailboxPresentMode) ? 1 : 0;
}

} // extern "C"
