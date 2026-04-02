// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#include "vulkan_bridge.h"
#include <vector>
#include <cstring>
#include <android/log.h>

#define LOG_TAG "osu!native-vulkan"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)

VulkanProbe::VulkanProbe() {
    if (createInstance()) {
        available_ = queryDevice();
    }

    if (!available_) {
        cleanup();
    }

    if (available_) {
        LOGI("Vulkan available: %s (Vendor: 0x%x, API %u.%u.%u, driver %u, VRAM %u MB, "
             "queues %u, mailbox=%d, vk1.3=%d, sync2=%d, presentWait=%d, gpl=%d, shaderObj=%d, priority=%d)",
             deviceInfo_.deviceName.c_str(),
             deviceInfo_.vendorId,
             VK_VERSION_MAJOR(deviceInfo_.apiVersion),
             VK_VERSION_MINOR(deviceInfo_.apiVersion),
             VK_VERSION_PATCH(deviceInfo_.apiVersion),
             deviceInfo_.driverVersion,
             deviceInfo_.deviceLocalMemoryMB,
             deviceInfo_.queueFamilyCount,
             deviceInfo_.supportsMailboxPresentMode ? 1 : 0,
             deviceInfo_.meetsVulkan13 ? 1 : 0,
             deviceInfo_.supportsSynchronization2 ? 1 : 0,
             deviceInfo_.supportsPresentWait ? 1 : 0,
             deviceInfo_.supportsGraphicsPipelineLibrary ? 1 : 0,
             deviceInfo_.supportsShaderObject ? 1 : 0,
             deviceInfo_.supportsGlobalPriority ? 1 : 0);
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
    appInfo.pEngineName = "No Engine";
    appInfo.engineVersion = VK_MAKE_VERSION(1, 0, 0);
    appInfo.apiVersion = VK_API_VERSION_1_3;

    VkInstanceCreateInfo createInfo{};
    createInfo.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    createInfo.pApplicationInfo = &appInfo;

    VkResult result = vkCreateInstance(&createInfo, nullptr, &instance_);

    if (result == VK_ERROR_INCOMPATIBLE_DRIVER) {
        appInfo.apiVersion = VK_API_VERSION_1_0;
        result = vkCreateInstance(&createInfo, nullptr, &instance_);
    }

    if (result != VK_SUCCESS) return false;
    return true;
}

bool VulkanProbe::queryDevice() {
    uint32_t deviceCount = 0;
    vkEnumeratePhysicalDevices(instance_, &deviceCount, nullptr);
    if (deviceCount == 0) return false;

    std::vector<VkPhysicalDevice> devices(deviceCount);
    if (vkEnumeratePhysicalDevices(instance_, &deviceCount, devices.data()) != VK_SUCCESS) return false;

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

    queryMemory(selected);
    queryQueueFamilies(selected);
    queryMailboxSupport(selected);
    queryVulkan13Features(selected);
    queryModernExtensions(selected);

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
    uint32_t count = 0;
    vkGetPhysicalDeviceQueueFamilyProperties(device, &count, nullptr);
    deviceInfo_.queueFamilyCount = count;
    std::vector<VkQueueFamilyProperties> families(count);
    vkGetPhysicalDeviceQueueFamilyProperties(device, &count, families.data());
    for (const auto& family : families) {
        if ((family.queueFlags & VK_QUEUE_COMPUTE_BIT) && !(family.queueFlags & VK_QUEUE_GRAPHICS_BIT))
            deviceInfo_.hasDedicatedComputeQueue = true;
        if ((family.queueFlags & VK_QUEUE_TRANSFER_BIT) && !(family.queueFlags & (VK_QUEUE_GRAPHICS_BIT | VK_QUEUE_COMPUTE_BIT)))
            deviceInfo_.hasDedicatedTransferQueue = true;
    }
}

void VulkanProbe::queryMailboxSupport(VkPhysicalDevice device) {
    uint32_t count = 0;
    vkEnumerateDeviceExtensionProperties(device, nullptr, &count, nullptr);
    std::vector<VkExtensionProperties> exts(count);
    vkEnumerateDeviceExtensionProperties(device, nullptr, &count, exts.data());
    for (const auto& ext : exts) {
        if (strcmp(ext.extensionName, VK_KHR_SWAPCHAIN_EXTENSION_NAME) == 0) {
            deviceInfo_.supportsMailboxPresentMode = true;
            break;
        }
    }
}

void VulkanProbe::queryModernExtensions(VkPhysicalDevice device) {
    uint32_t count = 0;
    vkEnumerateDeviceExtensionProperties(device, nullptr, &count, nullptr);
    std::vector<VkExtensionProperties> exts(count);
    vkEnumerateDeviceExtensionProperties(device, nullptr, &count, exts.data());
    for (const auto& ext : exts) {
        if (strcmp(ext.extensionName, VK_KHR_SWAPCHAIN_EXTENSION_NAME) == 0) deviceInfo_.supportsSwapchain = true;
        if (strcmp(ext.extensionName, VK_KHR_PRESENT_ID_EXTENSION_NAME) == 0) deviceInfo_.supportsPresentId = true;
        if (strcmp(ext.extensionName, VK_KHR_PRESENT_WAIT_EXTENSION_NAME) == 0) deviceInfo_.supportsPresentWait = true;
        if (strcmp(ext.extensionName, VK_EXT_GRAPHICS_PIPELINE_LIBRARY_EXTENSION_NAME) == 0) deviceInfo_.supportsGraphicsPipelineLibrary = true;
        if (strcmp(ext.extensionName, VK_EXT_SHADER_OBJECT_EXTENSION_NAME) == 0) deviceInfo_.supportsShaderObject = true;
        if (strcmp(ext.extensionName, VK_EXT_GLOBAL_PRIORITY_EXTENSION_NAME) == 0 || strcmp(ext.extensionName, VK_KHR_GLOBAL_PRIORITY_EXTENSION_NAME) == 0) deviceInfo_.supportsGlobalPriority = true;
        if (strcmp(ext.extensionName, VK_EXT_MEMORY_BUDGET_EXTENSION_NAME) == 0) deviceInfo_.supportsMemoryBudget = true;
    }

    // Adreno 740 (S23 Ultra) known flickering issues with present_id/wait extensions.
    // Adreno vendor ID is 0x5143 (Qualcomm).
    if (deviceInfo_.vendorId == 0x5143 && (deviceInfo_.deviceName.find("740") != std::string::npos || deviceInfo_.deviceName.find("Adreno") != std::string::npos)) {
        LOGI("Adreno GPU detected: disabling present_id and present_wait to prevent flickering/glitching");
        deviceInfo_.supportsPresentId = false;
        deviceInfo_.supportsPresentWait = false;
    }
}

void VulkanProbe::queryVulkan13Features(VkPhysicalDevice device) {
    if (VK_VERSION_MAJOR(deviceInfo_.apiVersion) < 1 || (VK_VERSION_MAJOR(deviceInfo_.apiVersion) == 1 && VK_VERSION_MINOR(deviceInfo_.apiVersion) < 3)) return;
    deviceInfo_.meetsVulkan13 = true;
    VkPhysicalDeviceVulkan13Features f13{};
    f13.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES;
    VkPhysicalDeviceFeatures2 f2{};
    f2.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2;
    f2.pNext = &f13;
    vkGetPhysicalDeviceFeatures2(device, &f2);
    deviceInfo_.supportsDynamicRendering = f13.dynamicRendering == VK_TRUE;
    deviceInfo_.supportsSynchronization2 = f13.synchronization2 == VK_TRUE;
}

void VulkanProbe::cleanup() {
    if (instance_ != VK_NULL_HANDLE) {
        vkDestroyInstance(instance_, nullptr);
        instance_ = VK_NULL_HANDLE;
    }
}

#define OSU_EXPORT __attribute__((visibility("default")))

extern "C" {
OSU_EXPORT intptr_t nVulkanProbeCreate() { return reinterpret_cast<intptr_t>(new (std::nothrow) VulkanProbe()); }
OSU_EXPORT void nVulkanProbeDestroy(intptr_t ptr) { if (ptr) delete reinterpret_cast<VulkanProbe*>(ptr); }
OSU_EXPORT byte nVulkanIsAvailable(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->isAvailable()) ? 1 : 0; }
OSU_EXPORT int nVulkanGetApiVersion(intptr_t ptr) { return ptr ? (int)reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().apiVersion : 0; }
OSU_EXPORT byte nVulkanSupportsSwapchain(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().supportsSwapchain) ? 1 : 0; }
OSU_EXPORT int nVulkanGetDeviceLocalMemoryMB(intptr_t ptr) { return ptr ? (int)reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().deviceLocalMemoryMB : 0; }
OSU_EXPORT int nVulkanGetQueueFamilyCount(intptr_t ptr) { return ptr ? (int)reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().queueFamilyCount : 0; }
OSU_EXPORT byte nVulkanHasDedicatedComputeQueue(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().hasDedicatedComputeQueue) ? 1 : 0; }
OSU_EXPORT byte nVulkanHasDedicatedTransferQueue(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().hasDedicatedTransferQueue) ? 1 : 0; }
OSU_EXPORT byte nVulkanSupportsMailboxPresentMode(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().supportsMailboxPresentMode) ? 1 : 0; }
OSU_EXPORT byte nVulkanMeetsVulkan13(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().meetsVulkan13) ? 1 : 0; }
OSU_EXPORT byte nVulkanSupportsDynamicRendering(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().supportsDynamicRendering) ? 1 : 0; }
OSU_EXPORT byte nVulkanSupportsSynchronization2(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().supportsSynchronization2) ? 1 : 0; }
OSU_EXPORT byte nVulkanSupportsPresentId(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().supportsPresentId) ? 1 : 0; }
OSU_EXPORT byte nVulkanSupportsPresentWait(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().supportsPresentWait) ? 1 : 0; }
OSU_EXPORT byte nVulkanSupportsGraphicsPipelineLibrary(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().supportsGraphicsPipelineLibrary) ? 1 : 0; }
OSU_EXPORT byte nVulkanSupportsShaderObject(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().supportsShaderObject) ? 1 : 0; }
OSU_EXPORT byte nVulkanSupportsGlobalPriority(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().supportsGlobalPriority) ? 1 : 0; }
OSU_EXPORT byte nVulkanSupportsMemoryBudget(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().supportsMemoryBudget) ? 1 : 0; }
}
