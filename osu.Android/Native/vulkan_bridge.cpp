// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#include "vulkan_bridge.h"
#include <android/log.h>
#include <vector>
#include <cstring>
#include <set>

#define LOG_TAG "osu!native"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

VulkanProbe::VulkanProbe() {
    available_ = createInstance() && queryDevice();

    if (!available_) {
        cleanup();
    }

    if (available_) {
        LOGI("Vulkan available: %s (API %u.%u.%u, driver %u)",
             deviceInfo_.deviceName.c_str(),
             VK_VERSION_MAJOR(deviceInfo_.apiVersion),
             VK_VERSION_MINOR(deviceInfo_.apiVersion),
             VK_VERSION_PATCH(deviceInfo_.apiVersion),
             deviceInfo_.driverVersion);
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
        return true;
    }

    std::vector<VkExtensionProperties> extensions(extCount);

    if (vkEnumerateDeviceExtensionProperties(selected, nullptr, &extCount, extensions.data()) != VK_SUCCESS) {
        deviceInfo_.supportsSwapchain = false;
        return true;
    }

    deviceInfo_.supportsSwapchain = false;

    for (const auto& ext : extensions) {
        if (strcmp(ext.extensionName, VK_KHR_SWAPCHAIN_EXTENSION_NAME) == 0) {
            deviceInfo_.supportsSwapchain = true;
            break;
        }
    }

    return true;
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
extern "C" {

long nVulkanProbeCreate() {
    auto* probe = new (std::nothrow) VulkanProbe();
    return reinterpret_cast<long>(probe);
}

void nVulkanProbeDestroy(long ptr) {
    if (ptr) delete reinterpret_cast<VulkanProbe*>(ptr);
}

unsigned char nVulkanIsAvailable(long ptr) {
    auto* probe = reinterpret_cast<VulkanProbe*>(ptr);
    return (probe && probe->isAvailable()) ? 1 : 0;
}

int nVulkanGetApiVersion(long ptr) {
    auto* probe = reinterpret_cast<VulkanProbe*>(ptr);
    return probe ? static_cast<int>(probe->getDeviceInfo().apiVersion) : 0;
}

unsigned char nVulkanSupportsSwapchain(long ptr) {
    auto* probe = reinterpret_cast<VulkanProbe*>(ptr);
    return (probe && probe->getDeviceInfo().supportsSwapchain) ? 1 : 0;
}

} // extern "C"
