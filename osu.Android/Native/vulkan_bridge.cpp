// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#include "vulkan_bridge.h"
#include <android/log.h>
#include <vector>
#include <cstring>
#include <new>

#define LOG_TAG "osu!native"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

VulkanProbe::VulkanProbe() {
    if (!createInstance()) {
        LOGE("Failed to create Vulkan instance for probing");
        return;
    }

    if (!queryDevice()) {
        LOGE("Failed to query Vulkan physical device info");
        cleanup();
        return;
    }

    available_ = true;

    LOGI("Vulkan available: %s (Vendor: 0x%x, API %u.%u.%u, driver %u, VRAM %u MB, "
         "qCount %u, vk1.3 %d, vk1.4 %d, sync2 %d, pWait %d, gpl %d, sObj %d, gPrio %d, "
         "hostCopy %d, pushDesc %d)",
         deviceInfo_.deviceName.c_str(),
         deviceInfo_.vendorId,
         VK_VERSION_MAJOR(deviceInfo_.apiVersion),
         VK_VERSION_MINOR(deviceInfo_.apiVersion),
         VK_VERSION_PATCH(deviceInfo_.apiVersion),
         deviceInfo_.driverVersion,
         deviceInfo_.deviceLocalMemoryMB,
         deviceInfo_.queueFamilyCount,
         deviceInfo_.meetsVulkan13 ? 1 : 0,
         deviceInfo_.meetsVulkan14 ? 1 : 0,
         deviceInfo_.supportsSynchronization2 ? 1 : 0,
         deviceInfo_.supportsPresentWait ? 1 : 0,
         deviceInfo_.supportsGraphicsPipelineLibrary ? 1 : 0,
         deviceInfo_.supportsShaderObject ? 1 : 0,
         deviceInfo_.supportsGlobalPriority ? 1 : 0,
         deviceInfo_.supportsHostImageCopy ? 1 : 0,
         deviceInfo_.supportsPushDescriptors ? 1 : 0);
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
    if (vkEnumeratePhysicalDevices(instance_, &deviceCount, nullptr) != VK_SUCCESS) return false;
    if (deviceCount == 0) return false;

    std::vector<VkPhysicalDevice> devices(deviceCount);
    if (vkEnumeratePhysicalDevices(instance_, &deviceCount, devices.data()) != VK_SUCCESS) return false;
    if (deviceCount == 0) return false;

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
    // Query extensions first (sets extension-based feature flags for pre-1.3 devices).
    queryModernExtensions(selected);
    // Feature queries for 1.3+ override extension-based values with actual feature support.
    queryVulkan13Features(selected);

    // MAILBOX present mode cannot be queried without a VkSurfaceKHR (we have none in
    // this lightweight probe).  The actual present mode is selected by the renderer
    // (Veldrid) at swapchain creation time via vkGetPhysicalDeviceSurfacePresentModesKHR.
    // We leave supportsMailboxPresentMode = false (default) to avoid false positives.

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
    if (count == 0) return;
    std::vector<VkQueueFamilyProperties> families(count);
    vkGetPhysicalDeviceQueueFamilyProperties(device, &count, families.data());
    for (const auto& family : families) {
        if ((family.queueFlags & VK_QUEUE_COMPUTE_BIT) && !(family.queueFlags & VK_QUEUE_GRAPHICS_BIT))
            deviceInfo_.hasDedicatedComputeQueue = true;
        if ((family.queueFlags & VK_QUEUE_TRANSFER_BIT) && !(family.queueFlags & (VK_QUEUE_GRAPHICS_BIT | VK_QUEUE_COMPUTE_BIT)))
            deviceInfo_.hasDedicatedTransferQueue = true;
    }
}

void VulkanProbe::queryModernExtensions(VkPhysicalDevice device) {
    uint32_t count = 0;
    if (vkEnumerateDeviceExtensionProperties(device, nullptr, &count, nullptr) != VK_SUCCESS) return;
    if (count == 0) return;
    std::vector<VkExtensionProperties> exts(count);
    if (vkEnumerateDeviceExtensionProperties(device, nullptr, &count, exts.data()) != VK_SUCCESS) return;
    for (const auto& ext : exts) {
        if (strcmp(ext.extensionName, VK_KHR_SWAPCHAIN_EXTENSION_NAME) == 0) deviceInfo_.supportsSwapchain = true;
        if (strcmp(ext.extensionName, VK_KHR_PRESENT_ID_EXTENSION_NAME) == 0) deviceInfo_.supportsPresentId = true;
        if (strcmp(ext.extensionName, VK_KHR_PRESENT_WAIT_EXTENSION_NAME) == 0) deviceInfo_.supportsPresentWait = true;
        if (strcmp(ext.extensionName, VK_EXT_GRAPHICS_PIPELINE_LIBRARY_EXTENSION_NAME) == 0) deviceInfo_.supportsGraphicsPipelineLibrary = true;
        if (strcmp(ext.extensionName, VK_EXT_SHADER_OBJECT_EXTENSION_NAME) == 0) deviceInfo_.supportsShaderObject = true;
        if (strcmp(ext.extensionName, VK_EXT_GLOBAL_PRIORITY_EXTENSION_NAME) == 0 || strcmp(ext.extensionName, VK_KHR_GLOBAL_PRIORITY_EXTENSION_NAME) == 0) deviceInfo_.supportsGlobalPriority = true;
        if (strcmp(ext.extensionName, VK_EXT_MEMORY_BUDGET_EXTENSION_NAME) == 0) deviceInfo_.supportsMemoryBudget = true;
        if (strcmp(ext.extensionName, "VK_EXT_surface_maintenance1") == 0) deviceInfo_.supportsSurfaceMaintenance1 = true;

        // Extension-based fallback for pre-1.3 devices (overridden by queryVulkan13Features on 1.3+).
        if (strcmp(ext.extensionName, "VK_KHR_dynamic_rendering") == 0) deviceInfo_.supportsDynamicRendering = true;
        if (strcmp(ext.extensionName, "VK_KHR_synchronization2") == 0) deviceInfo_.supportsSynchronization2 = true;

        // Vulkan 1.4+ / Android 16+ extensions — used by string literals since NDK r29
        // headers may not define these macros.
        if (strcmp(ext.extensionName, "VK_EXT_host_image_copy") == 0) deviceInfo_.supportsHostImageCopy = true;
        if (strcmp(ext.extensionName, "VK_KHR_push_descriptor") == 0) deviceInfo_.supportsPushDescriptors = true;
    }

    // ── Vendor-specific GPU quirks ──────────────────────────────────────
    // Qualcomm Adreno 7xx series: known flickering with PresentId/PresentWait
    // and broken Graphics Pipeline Library compilation on some driver versions.
    // Only target 7xx (730/740/750) — other Adreno generations are unaffected.
    if (deviceInfo_.vendorId == 0x5143) {
        const auto& name = deviceInfo_.deviceName;
        bool isAdreno7xx = name.find("730") != std::string::npos ||
                           name.find("740") != std::string::npos ||
                           name.find("750") != std::string::npos;
        if (isAdreno7xx) {
            LOGI("Adreno 7xx GPU detected: applying performance and flickering overrides");
            deviceInfo_.disablePresentId = true;
            deviceInfo_.disablePresentWait = true;
            deviceInfo_.disableGraphicsPipelineLibrary = true;
        }
    }

    // ARM Mali (Samsung Exynos, MediaTek Dimensity, Google Tensor):
    // Vendor ID 0x13B5 = ARM. Early Mali-G710/G715/G720 drivers have buggy
    // Graphics Pipeline Library support that causes shader compilation stalls.
    if (deviceInfo_.vendorId == 0x13B5) {
        if (deviceInfo_.deviceName.find("Mali") != std::string::npos) {
            LOGI("ARM Mali GPU detected: applying vendor quirks");
            // Mali GPUs commonly report GPL support but the implementation
            // causes stalls on pipeline creation. Disable to avoid hitching.
            deviceInfo_.disableGraphicsPipelineLibrary = true;
        }
    }

    // Imagination Technologies PowerVR (older Samsung, some MediaTek):
    // Vendor ID 0x1010 = ImgTec. Disable advanced features for stability.
    if (deviceInfo_.vendorId == 0x1010) {
        LOGI("PowerVR GPU detected: disabling advanced Vulkan features");
        deviceInfo_.disablePresentId = true;
        deviceInfo_.disablePresentWait = true;
        deviceInfo_.disableGraphicsPipelineLibrary = true;
    }
}

void VulkanProbe::queryVulkan13Features(VkPhysicalDevice device) {
    if (VK_VERSION_MAJOR(deviceInfo_.apiVersion) < 1 || (VK_VERSION_MAJOR(deviceInfo_.apiVersion) == 1 && VK_VERSION_MINOR(deviceInfo_.apiVersion) < 3)) return;
    deviceInfo_.meetsVulkan13 = true;

    // Vulkan 1.4 is just a version check — no NDK header support needed.
    if (VK_VERSION_MINOR(deviceInfo_.apiVersion) >= 4)
        deviceInfo_.meetsVulkan14 = true;

    VkPhysicalDeviceVulkan13Features f13{};
    f13.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES;
    VkPhysicalDeviceFeatures2 f2{};
    f2.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2;
    f2.pNext = &f13;
    vkGetPhysicalDeviceFeatures2(device, &f2);
    // Override extension-based flags with accurate feature queries for 1.3+ devices.
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
OSU_EXPORT byte nVulkanSupportsSurfaceMaintenance1(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().supportsSurfaceMaintenance1) ? 1 : 0; }
OSU_EXPORT byte nVulkanDisablePresentId(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().disablePresentId) ? 1 : 0; }
OSU_EXPORT byte nVulkanDisablePresentWait(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().disablePresentWait) ? 1 : 0; }
OSU_EXPORT byte nVulkanDisableGraphicsPipelineLibrary(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().disableGraphicsPipelineLibrary) ? 1 : 0; }
OSU_EXPORT byte nVulkanMeetsVulkan14(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().meetsVulkan14) ? 1 : 0; }
OSU_EXPORT byte nVulkanSupportsHostImageCopy(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().supportsHostImageCopy) ? 1 : 0; }
OSU_EXPORT byte nVulkanSupportsPushDescriptors(intptr_t ptr) { return (ptr && reinterpret_cast<VulkanProbe*>(ptr)->getDeviceInfo().supportsPushDescriptors) ? 1 : 0; }
}
