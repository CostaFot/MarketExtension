; Inno Setup Script for Markets Extension for Command Palette

#define AppVersion "0.0.1.0"

[Setup]
AppId={{6b38c9aa-bbee-45e9-81e9-cf25707910e7}}
AppName=Markets Extension for Command Palette
AppVersion={#AppVersion}
AppPublisher=Costa Fotiadis
DefaultDirName={localappdata}\MarketExtension
PrivilegesRequired=lowest
OutputDir=bin\Release\installer
OutputBaseFilename=MarketExtension-Setup-{#AppVersion}
Compression=lzma
SolidCompression=yes
MinVersion=10.0.19041

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "bin\Release\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\Markets Extension for Command Palette"; Filename: "{app}\MarketExtension.exe"

[Registry]
Root: HKCU; Subkey: "SOFTWARE\Classes\CLSID\{{6b38c9aa-bbee-45e9-81e9-cf25707910e7}}"; ValueType: string; ValueName: ""; ValueData: "MarketExtension"
Root: HKCU; Subkey: "SOFTWARE\Classes\CLSID\{{6b38c9aa-bbee-45e9-81e9-cf25707910e7}}\LocalServer32"; ValueType: string; ValueName: ""; ValueData: "{app}\MarketExtension.exe -RegisterProcessAsComServer"
