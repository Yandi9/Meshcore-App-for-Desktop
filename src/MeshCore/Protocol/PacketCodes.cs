namespace MeshCore;

/// <summary>Command codes sent from the app to the companion radio.</summary>
public enum CommandCode : byte
{
    AppStart = 0x01,
    SendMessage = 0x02,
    SendChannelMessage = 0x03,
    GetContacts = 0x04,
    GetTime = 0x05,
    SetTime = 0x06,
    SendAdvertisement = 0x07,
    SetName = 0x08,
    UpdateContact = 0x09,
    GetMessage = 0x0A,
    SetRadio = 0x0B,
    SetTxPower = 0x0C,
    ResetPath = 0x0D,
    SetCoordinates = 0x0E,
    RemoveContact = 0x0F,
    ShareContact = 0x10,
    ExportContact = 0x11,
    ImportContact = 0x12,
    Reboot = 0x13,
    GetBattery = 0x14,
    SetTuning = 0x15,
    DeviceQuery = 0x16,
    ExportPrivateKey = 0x17,
    ImportPrivateKey = 0x18,
    SendRawData = 0x19,
    SendLogin = 0x1A,
    SendStatusRequest = 0x1B,
    HasConnection = 0x1C,
    SendLogout = 0x1D,
    GetContactByKey = 0x1E,
    GetChannel = 0x1F,
    SetChannel = 0x20,
    SignStart = 0x21,
    SignData = 0x22,
    SignFinish = 0x23,
    SendTrace = 0x24,
    SetDevicePin = 0x25,
    SetOtherParams = 0x26,
    GetSelfTelemetry = 0x27,
    GetCustomVars = 0x28,
    SetCustomVar = 0x29,
    GetAdvertPath = 0x2A,
    GetTuningParams = 0x2B,
    BinaryRequest = 0x32,
    FactoryReset = 0x33,
    PathDiscovery = 0x34,
    SetFloodScope = 0x36,
    SendControlData = 0x37,
    GetStats = 0x38,
    SendAnonReq = 0x39,
    SetAutoAddConfig = 0x3A,
    GetAutoAddConfig = 0x3B,
    GetRepeatFreq = 0x3C,
    SetPathHashMode = 0x3D,
    SendChannelData = 0x3E,
    SetDefaultFloodScope = 0x3F,
    GetDefaultFloodScope = 0x40,
    SendRawPacket = 0x41,
}

/// <summary>Response and push codes received from the companion radio.</summary>
public enum ResponseCode : byte
{
    Ok = 0x00,
    Error = 0x01,
    ContactStart = 0x02,
    Contact = 0x03,
    ContactEnd = 0x04,
    SelfInfo = 0x05,
    MessageSent = 0x06,
    ContactMessageReceived = 0x07,
    ChannelMessageReceived = 0x08,
    CurrentTime = 0x09,
    NoMoreMessages = 0x0A,
    ContactUri = 0x0B,
    Battery = 0x0C,
    DeviceInfo = 0x0D,
    PrivateKey = 0x0E,
    Disabled = 0x0F,
    ContactMessageReceivedV3 = 0x10,
    ChannelMessageReceivedV3 = 0x11,
    ChannelInfo = 0x12,
    SignStart = 0x13,
    Signature = 0x14,
    CustomVars = 0x15,
    AdvertPath = 0x16,
    TuningParams = 0x17,
    Stats = 0x18,
    AutoAddConfig = 0x19,
    AllowedRepeatFreq = 0x1A,
    ChannelDataReceived = 0x1B,
    DefaultFloodScope = 0x1C,

    // Push notifications (0x80+)
    Advertisement = 0x80,
    PathUpdate = 0x81,
    Ack = 0x82,
    MessagesWaiting = 0x83,
    RawData = 0x84,
    LoginSuccess = 0x85,
    LoginFailed = 0x86,
    StatusResponse = 0x87,
    LogData = 0x88,
    TraceData = 0x89,
    NewAdvertisement = 0x8A,
    TelemetryResponse = 0x8B,
    BinaryResponse = 0x8C,
    PathDiscoveryResponse = 0x8D,
    ControlData = 0x8E,
    ContactDeleted = 0x8F,
    ContactsFull = 0x90,
}

public enum BinaryRequestType : byte
{
    Status = 0x01,
    KeepAlive = 0x02,
    Telemetry = 0x03,
    Mma = 0x04,
    Acl = 0x05,
    Neighbours = 0x06,
    OwnerInfo = 0x07,
}

public enum AnonRequestType : byte
{
    Regions = 0x01,
    Owner = 0x02,
    Basic = 0x03,
}

public enum ControlType : byte
{
    NodeDiscoverRequest = 0x80,
    NodeDiscoverResponse = 0x90,
}

public enum StatsType : byte
{
    Core = 0x00,
    Radio = 0x01,
    Packets = 0x02,
}

public enum TextType : byte
{
    Plain = 0x00,
    CliData = 0x01,
    Signed = 0x02,
}

/// <summary>Firmware error codes carried by <see cref="ResponseCode.Error"/>.</summary>
public enum ErrorCode : byte
{
    UnsupportedCommand = 1,
    NotFound = 2,
    TableFull = 3,
    BadState = 4,
    FileIoError = 5,
    IllegalArgument = 6,
}
