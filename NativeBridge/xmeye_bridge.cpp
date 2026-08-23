#include <windows.h>
#include <cstring>

namespace {
struct QtValue { void* data = nullptr; };

template <typename T>
T proc(HMODULE module, const char* name) {
    return reinterpret_cast<T>(GetProcAddress(module, name));
}
}

extern "C" __declspec(dllexport) int __cdecl XMEye_HttpPost(
    const char* url, const char* body, int requestType, char* response, int responseCapacity) {
    if (!url || !body || !response || responseCapacity <= 0) return -9001;
    response[0] = '\0';

    HMODULE qt = GetModuleHandleW(L"Qt5Core.dll");
    HMODULE cms = GetModuleHandleW(L"CMSClient.dll");
    if (!qt || !cms) return -9002;

    using QStringCtor = void* (__cdecl*)(void*, const char*);
    using QStringDefaultCtor = void* (__cdecl*)(void*);
    using QStringDtor = void (__cdecl*)(void*);
    using ToUtf8 = void* (__cdecl*)(void*, void*);
    using ByteArrayData = char* (__cdecl*)(void*);
    using ByteArrayDtor = void (__cdecl*)(void*);
    using HttpPost = int (__cdecl*)(void*, void*, void*, int);

    auto stringCtor = proc<QStringCtor>(qt, "??0QString@@QEAA@PEBD@Z");
    auto stringDefaultCtor = proc<QStringDefaultCtor>(qt, "??0QString@@QEAA@XZ");
    auto stringDtor = proc<QStringDtor>(qt, "??1QString@@QEAA@XZ");
    auto toUtf8 = proc<ToUtf8>(qt, "?toUtf8@QString@@QEGBA?AVQByteArray@@XZ");
    auto byteArrayData = proc<ByteArrayData>(qt, "?data@QByteArray@@QEAAPEADXZ");
    auto byteArrayDtor = proc<ByteArrayDtor>(qt, "??1QByteArray@@QEAA@XZ");
    auto httpPost = proc<HttpPost>(cms, "CMS_Client_HttpPost");
    if (!stringCtor || !stringDefaultCtor || !stringDtor || !toUtf8 ||
        !byteArrayData || !byteArrayDtor || !httpPost) return -9003;

    QtValue qUrl{}, qBody{}, qResponse{}, bytes{};
    stringCtor(&qUrl, url);
    stringCtor(&qBody, body);
    stringDefaultCtor(&qResponse);

    int result = httpPost(&qUrl, &qBody, &qResponse, requestType);
    toUtf8(&qResponse, &bytes);
    const char* text = byteArrayData(&bytes);
    if (text) strncpy_s(response, static_cast<size_t>(responseCapacity), text, _TRUNCATE);

    byteArrayDtor(&bytes);
    stringDtor(&qResponse);
    // CMS_Client_HttpPost takes the first two QStrings by value and consumes
    // their ABI temporaries. Destroying qUrl/qBody here would double-free them.
    return result;
}

extern "C" __declspec(dllexport) int __cdecl XMEye_HttpGet(
    const char* url, int requestType, char* response, int responseCapacity) {
    if (!url || !response || responseCapacity <= 0) return -9201;
    response[0] = '\0';

    HMODULE qt = GetModuleHandleW(L"Qt5Core.dll");
    HMODULE cms = GetModuleHandleW(L"CMSClient.dll");
    if (!qt || !cms) return -9202;

    using QStringCtor = void* (__cdecl*)(void*, const char*);
    using QStringDefaultCtor = void* (__cdecl*)(void*);
    using QStringDtor = void (__cdecl*)(void*);
    using ToUtf8 = void* (__cdecl*)(void*, void*);
    using ByteArrayData = char* (__cdecl*)(void*);
    using ByteArrayDtor = void (__cdecl*)(void*);
    using HttpGet = int (__cdecl*)(void*, void*, int);

    auto stringCtor = proc<QStringCtor>(qt, "??0QString@@QEAA@PEBD@Z");
    auto stringDefaultCtor = proc<QStringDefaultCtor>(qt, "??0QString@@QEAA@XZ");
    auto stringDtor = proc<QStringDtor>(qt, "??1QString@@QEAA@XZ");
    auto toUtf8 = proc<ToUtf8>(qt, "?toUtf8@QString@@QEGBA?AVQByteArray@@XZ");
    auto byteArrayData = proc<ByteArrayData>(qt, "?data@QByteArray@@QEAAPEADXZ");
    auto byteArrayDtor = proc<ByteArrayDtor>(qt, "??1QByteArray@@QEAA@XZ");
    auto httpGet = proc<HttpGet>(cms, "CMS_Client_HttpGet");
    if (!stringCtor || !stringDefaultCtor || !stringDtor || !toUtf8 ||
        !byteArrayData || !byteArrayDtor || !httpGet) return -9203;

    QtValue qUrl{}, qResponse{}, bytes{};
    stringCtor(&qUrl, url);
    stringDefaultCtor(&qResponse);
    int result = httpGet(&qUrl, &qResponse, requestType);
    toUtf8(&qResponse, &bytes);
    const char* text = byteArrayData(&bytes);
    if (text) strncpy_s(response, static_cast<size_t>(responseCapacity), text, _TRUNCATE);
    byteArrayDtor(&bytes);
    stringDtor(&qResponse);
    // CMS_Client_HttpGet consumes its first QString value.
    return result;
}
