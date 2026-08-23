#include <windows.h>
#include <cstring>
#include <cwctype>
#include <string>

namespace {
struct QtValue { void* data = nullptr; };

template <typename T>
T proc(HMODULE module, const char* name) {
    return reinterpret_cast<T>(GetProcAddress(module, name));
}

std::wstring qtDiagnosticPath;
volatile LONG deviceStoreConfiguration = 0;

std::wstring readQString(const void* value) {
    if (!value) return {};
    const auto data = *reinterpret_cast<const unsigned char* const*>(value);
    if (!data) return {};
    const int size = *reinterpret_cast<const int*>(data + 4);
    const ptrdiff_t offset = *reinterpret_cast<const ptrdiff_t*>(data + 16);
    if (size <= 0 || size > 16384 || offset < 0 || offset > 1048576) return {};
    const auto chars = reinterpret_cast<const wchar_t*>(data + offset);
    return std::wstring(chars, chars + size);
}

void appendQtDiagnostic(const wchar_t* category) {
    if (qtDiagnosticPath.empty()) return;
    HANDLE file = CreateFileW(qtDiagnosticPath.c_str(), FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    std::wstring line = L"[Qt/SQLite] ";
    line += category;
    line += L"\r\n";
    DWORD written = 0;
    WriteFile(file, line.data(), static_cast<DWORD>(line.size() * sizeof(wchar_t)), &written, nullptr);
    CloseHandle(file);
}

void __cdecl qtMessageHandler(int, const void*, const void* message) {
    std::wstring messageText = readQString(message);
    for (auto& ch : messageText) ch = static_cast<wchar_t>(std::towlower(ch));
    if (messageText.find(L"driver not loaded") != std::wstring::npos)
        appendQtDiagnostic(L"driver nao carregado");
    else if (messageText.find(L"unable to open database file") != std::wstring::npos)
        appendQtDiagnostic(L"nao foi possivel abrir o arquivo do banco");
    else if (messageText.find(L"no such table") != std::wstring::npos)
        appendQtDiagnostic(L"tabela esperada nao existe");
    else if (messageText.find(L"database is locked") != std::wstring::npos)
        appendQtDiagnostic(L"banco bloqueado");
    else if (messageText.find(L"database path") != std::wstring::npos ||
             messageText.find(L"database name") != std::wstring::npos)
        appendQtDiagnostic(L"caminho do banco definido (ocultado)");
}
}

extern "C" __declspec(dllexport) int __cdecl XMEye_EnableQtDiagnostics(const wchar_t* path) {
    if (!path || !*path) return -9801;
    HMODULE qt = GetModuleHandleW(L"Qt5Core.dll");
    if (!qt) return -9802;
    using MessageHandler = void (__cdecl*)(int, const void*, const void*);
    using InstallHandler = MessageHandler (__cdecl*)(MessageHandler);
    auto install = proc<InstallHandler>(qt,
        "?qInstallMessageHandler@@YAP6AXW4QtMsgType@@AEBVQMessageLogContext@@AEBVQString@@@ZP6AX012@Z@Z");
    if (!install) return -9803;
    qtDiagnosticPath = path;
    DeleteFileW(path);
    install(qtMessageHandler);
    return 0;
}

extern "C" __declspec(dllexport) int __cdecl XMEye_EnableTransientDeviceStore() {
    HMODULE cms = GetModuleHandleW(L"CMSClient.dll");
    if (!cms) return -9901;

    // Resolve the private m_deviceSource flag from the verified instruction
    // that reads it in this exact CMS build. Using the instruction-relative
    // address avoids depending on the DLL load base (ASLR).
    auto instruction = reinterpret_cast<unsigned char*>(cms) + 0xB5C9;
    if (instruction[0] != 0x83 || instruction[1] != 0x3D || instruction[6] != 0x00)
        return -9902;
    const int displacement = *reinterpret_cast<const int*>(instruction + 2);
    auto source = reinterpret_cast<int*>(instruction + 7 + displacement);

    DWORD previousProtection = 0;
    if (!VirtualProtect(source, sizeof(int), PAGE_READWRITE, &previousProtection))
        return -9903;
    *source = 1;
    DWORD ignored = 0;
    VirtualProtect(source, sizeof(int), previousProtection, &ignored);
    return *source == 1 ? 0 : -9904;
}

extern "C" __declspec(dllexport) int __cdecl XMEye_ConfigureInMemoryDeviceStore() {
    LONG existing = InterlockedCompareExchange(&deviceStoreConfiguration, 1, 0);
    if (existing == 2) return 0;
    if (existing != 0) return -9950;
    const auto fail = [](int code) {
        InterlockedExchange(&deviceStoreConfiguration, 0);
        return code;
    };

    HMODULE qtCore = GetModuleHandleW(L"Qt5Core.dll");
    HMODULE qtSql = GetModuleHandleW(L"Qt5Sql.dll");
    HMODULE cms = GetModuleHandleW(L"CMSClient.dll");
    if (!qtCore || !qtSql || !cms) return fail(-9951);

    using QStringCtor = void* (__cdecl*)(void*, const char*);
    using QStringDtor = void (__cdecl*)(void*);
    using AddDatabase = void* (__cdecl*)(void*, const void*, const void*);
    using DatabaseAssign = void* (__cdecl*)(void*, const void*);
    using DatabaseDtor = void (__cdecl*)(void*);
    using SetDatabaseName = void (__cdecl*)(void*, const void*);
    using OpenDatabase = bool (__cdecl*)(void*);
    using DeviceManagerInstance = void* (__cdecl*)();
    using CreateTable = void (__cdecl*)(void*, int);

    auto stringCtor = proc<QStringCtor>(qtCore, "??0QString@@QEAA@PEBD@Z");
    auto stringDtor = proc<QStringDtor>(qtCore, "??1QString@@QEAA@XZ");
    auto addDatabase = proc<AddDatabase>(qtSql,
        "?addDatabase@QSqlDatabase@@SA?AV1@AEBVQString@@0@Z");
    auto databaseAssign = proc<DatabaseAssign>(qtSql,
        "??4QSqlDatabase@@QEAAAEAV0@AEBV0@@Z");
    auto databaseDtor = proc<DatabaseDtor>(qtSql, "??1QSqlDatabase@@QEAA@XZ");
    auto setDatabaseName = proc<SetDatabaseName>(qtSql,
        "?setDatabaseName@QSqlDatabase@@QEAAXAEBVQString@@@Z");
    auto openDatabase = proc<OpenDatabase>(qtSql, "?open@QSqlDatabase@@QEAA_NXZ");
    if (!stringCtor || !stringDtor || !addDatabase || !databaseAssign ||
        !databaseDtor || !setDatabaseName || !openDatabase) return fail(-9952);

    auto managerInstance = reinterpret_cast<DeviceManagerInstance>(
        reinterpret_cast<unsigned char*>(cms) + 0x13820);
    auto createTable = reinterpret_cast<CreateTable>(
        reinterpret_cast<unsigned char*>(cms) + 0x13660);
    void* manager = managerInstance();
    if (!manager) return fail(-9953);

    QtValue driver{}, connection{}, database{}, databaseName{};
    stringCtor(&driver, "QSQLITE");
    // CMS pode criar a conexao "devices" de forma assincrona. Substitui-la
    // enquanto ainda esta em uso provoca um use-after-free dentro do Qt.
    stringCtor(&connection, "xmeye_cloud_memory");
    addDatabase(&database, &driver, &connection);
    void* managerDatabase = reinterpret_cast<unsigned char*>(manager) + 0x50;
    databaseAssign(managerDatabase, &database);
    stringCtor(&databaseName, ":memory:");
    setDatabaseName(managerDatabase, &databaseName);
    const bool opened = openDatabase(managerDatabase);
    stringDtor(&databaseName);
    databaseDtor(&database);
    stringDtor(&connection);
    stringDtor(&driver);
    if (!opened) return fail(-9954);

    auto sourceInstruction = reinterpret_cast<unsigned char*>(cms) + 0xB5C9;
    if (sourceInstruction[0] != 0x83 || sourceInstruction[1] != 0x3D)
        return fail(-9955);
    const int displacement = *reinterpret_cast<const int*>(sourceInstruction + 2);
    auto source = reinterpret_cast<int*>(sourceInstruction + 7 + displacement);
    *source = 0;
    createTable(manager, 0);
    InterlockedExchange(&deviceStoreConfiguration, 2);
    return 0;
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

extern "C" __declspec(dllexport) int __cdecl XMEye_HttpPostAuthorized(
    const char* url, const char* body, const char* authorization, int requestType,
    char* response, int responseCapacity) {
    if (!url || !body || !authorization || !response || responseCapacity <= 0) return -9101;
    response[0] = '\0';

    HMODULE qt = GetModuleHandleW(L"Qt5Core.dll");
    HMODULE cms = GetModuleHandleW(L"CMSClient.dll");
    if (!qt || !cms) return -9102;

    using QStringCtor = void* (__cdecl*)(void*, const char*);
    using QStringDtor = void (__cdecl*)(void*);
    using ToUtf8 = void* (__cdecl*)(void*, void*);
    using ByteArrayData = char* (__cdecl*)(void*);
    using ByteArrayDtor = void (__cdecl*)(void*);
    using HttpPost = int (__cdecl*)(void*, void*, void*, int);

    auto stringCtor = proc<QStringCtor>(qt, "??0QString@@QEAA@PEBD@Z");
    auto stringDtor = proc<QStringDtor>(qt, "??1QString@@QEAA@XZ");
    auto toUtf8 = proc<ToUtf8>(qt, "?toUtf8@QString@@QEGBA?AVQByteArray@@XZ");
    auto byteArrayData = proc<ByteArrayData>(qt, "?data@QByteArray@@QEAAPEADXZ");
    auto byteArrayDtor = proc<ByteArrayDtor>(qt, "??1QByteArray@@QEAA@XZ");
    auto httpPost = proc<HttpPost>(cms, "CMS_Client_HttpPost");
    if (!stringCtor || !stringDtor || !toUtf8 ||
        !byteArrayData || !byteArrayDtor || !httpPost) return -9103;

    QtValue qUrl{}, qBody{}, qResponse{}, bytes{};
    stringCtor(&qUrl, url);
    stringCtor(&qBody, body);
    // No tipo 2, a CMS usa o valor inicial deste parametro como o cabecalho
    // Authorization e depois o substitui pelo JSON retornado pelo servidor.
    stringCtor(&qResponse, authorization);

    int result = httpPost(&qUrl, &qBody, &qResponse, requestType);
    toUtf8(&qResponse, &bytes);
    const char* text = byteArrayData(&bytes);
    if (text) strncpy_s(response, static_cast<size_t>(responseCapacity), text, _TRUNCATE);

    byteArrayDtor(&bytes);
    stringDtor(&qResponse);
    // CMS_Client_HttpPost consome as copias ABI de qUrl e qBody.
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

extern "C" __declspec(dllexport) int __cdecl XMEye_InitAppInfo(
    const char* appId, const char* uuid, const char* secret, int moveCard) {
    if (!appId || !uuid || !secret) return -9401;

    HMODULE qt = GetModuleHandleW(L"Qt5Core.dll");
    HMODULE cms = GetModuleHandleW(L"CMSClient.dll");
    if (!qt || !cms) return -9402;

    using QStringCtor = void* (__cdecl*)(void*, const char*);
    using InitAppInfo = int (__cdecl*)(void*, void*, void*, int);
    auto stringCtor = proc<QStringCtor>(qt, "??0QString@@QEAA@PEBD@Z");
    auto initAppInfo = proc<InitAppInfo>(cms, "CMS_Client_InitAppinfo");
    if (!stringCtor || !initAppInfo) return -9403;

    QtValue qAppId{}, qUuid{}, qSecret{};
    stringCtor(&qAppId, appId);
    stringCtor(&qUuid, uuid);
    stringCtor(&qSecret, secret);
    return initAppInfo(&qAppId, &qUuid, &qSecret, moveCard);
}

extern "C" __declspec(dllexport) int __cdecl XMEye_SetHttpApiUrl(
    int apiType, const char* serviceHost, const char* amsHost) {
    if (!serviceHost || !amsHost) return -9601;

    HMODULE qt = GetModuleHandleW(L"Qt5Core.dll");
    HMODULE cms = GetModuleHandleW(L"CMSClient.dll");
    if (!qt || !cms) return -9602;

    using QStringCtor = void* (__cdecl*)(void*, const char*);
    using SetHttpApiUrl = int (__cdecl*)(int, void*, void*);
    auto stringCtor = proc<QStringCtor>(qt, "??0QString@@QEAA@PEBD@Z");
    auto setHttpApiUrl = proc<SetHttpApiUrl>(cms, "CMS_Client_SetHttpApiUrl");
    if (!stringCtor || !setHttpApiUrl) return -9603;

    QtValue qService{}, qAms{};
    stringCtor(&qService, serviceHost);
    stringCtor(&qAms, amsHost);
    return setHttpApiUrl(apiType, &qService, &qAms);
}

extern "C" __declspec(dllexport) int __cdecl XMEye_SetCloudToken(const char* token) {
    if (!token) return -9701;

    HMODULE qt = GetModuleHandleW(L"Qt5Core.dll");
    HMODULE cms = GetModuleHandleW(L"CMSClient.dll");
    if (!qt || !cms) return -9702;

    using QStringCtor = void* (__cdecl*)(void*, const char*);
    using SetCloudToken = int (__cdecl*)(void*);
    auto stringCtor = proc<QStringCtor>(qt, "??0QString@@QEAA@PEBD@Z");
    auto setCloudToken = proc<SetCloudToken>(cms, "CMS_Client_SetCloudToken");
    if (!stringCtor || !setCloudToken) return -9703;

    QtValue qToken{};
    stringCtor(&qToken, token);
    return setCloudToken(&qToken);
}

extern "C" __declspec(dllexport) int __cdecl XMEye_QueryDeviceStatus(const char* cloudId) {
    if (!cloudId || !*cloudId) return -9751;

    HMODULE qt = GetModuleHandleW(L"Qt5Core.dll");
    HMODULE cms = GetModuleHandleW(L"CMSClient.dll");
    if (!qt || !cms) return -9752;

    using QStringCtor = void* (__cdecl*)(void*, const char*);
    using QueryDeviceStatus = int (__cdecl*)(void*);
    auto stringCtor = proc<QStringCtor>(qt, "??0QString@@QEAA@PEBD@Z");
    auto queryDeviceStatus = proc<QueryDeviceStatus>(cms, "CMS_Client_QueryDevStatus");
    if (!stringCtor || !queryDeviceStatus) return -9753;

    QtValue qCloudId{};
    stringCtor(&qCloudId, cloudId);
    // O export recebe QString por valor e destroi a copia ABI internamente.
    return queryDeviceStatus(&qCloudId);
}
