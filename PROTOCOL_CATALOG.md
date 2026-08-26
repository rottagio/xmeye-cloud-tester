# Catálogo técnico VMS Pro / FunSDK

Este levantamento descreve o protocolo, não uma conta ou modelos específicos. Nenhum identificador, IP, usuário ou senha observado nos logs foi copiado para o código.

## Estrutura confirmada

| Função | Leitura | Gravação | Escopo |
|---|---:|---:|---|
| Configuração JSON comum | 1042 | 1040 | dispositivo ou canal |
| Identidade e armazenamento | 1020 | — | dispositivo |
| Capacidades (`SystemFunction`) | 1360 | — | dispositivo |
| Hora do dispositivo | 1452 | 1450 | dispositivo |
| Atualização de firmware | — | 2260 | dispositivo |

O nome JSON diferencia a operação dentro de `1042/1040`. Os nomes e metadados executáveis ficam em `DeviceConfigurationCatalog.cs`.

## Grupos mapeados

- Básicas: `General.General`, `General.Location`, `System.TimeZone`, `OPTimeQuery`.
- Armazenamento: `StorageInfo`, `Storage.Snapshot`, `OPStorageManager`.
- Gravação: `Record`, `ExtRecord`, `Storage.EpitomeRecord`.
- Alarme inteligente: `Detect.MotionDetect`, `Detect.HumanDetection`, `Alarm.PIR`, `Detect.DetectTrack`.
- Som e luz: `Camera.WhiteLight`, `Alarm.IntellAlertAlarm`, `Ability.VoiceTipType`, `fVideo.Volume`, `fVideo.VolumeIn`.
- Imagem e PTZ: `Camera.Param`, `Camera.ParamEx`, `Uart.PTZControlCmd`, `OPPTZControl`.
- Rede: `NetWork.Wifi`, `NetWork.NetNTP`, `NetWork.RTSP`.
- Sobre: `SystemInfo`, `SystemInfoEx`, `SystemFunction`, `OPFileUpgradeIPCReq`.

## Evidência utilizada

1. Logs instrumentados do VMS Pro: IDs, nomes JSON e respostas reais.
2. `ConfigModule.pdb` e binários do VMS Pro: telas Get/Save e operações suportadas.
3. Demo e classes do FunSDK 5.1.3a: nomes JSON e escopo por canal/dispositivo.
4. Ponte CMS já usada pelo aplicativo: comandos legados de armazenamento, gravação, luz e PTZ.

## Regras de segurança

- Abrir o aplicativo não executa comandos de gravação deste catálogo.
- Operações destrutivas (`OPStorageManager` e atualização) nunca são automáticas.
- Rede é classificada como gravação sensível.
- O catálogo informa que o protocolo existe; o perfil individual só marca suporte após `SystemFunction` ou resposta direta válida.
- Evidências são vinculadas ao firmware e descartadas quando o firmware muda.
- O fluxo de conexão e reconexão não faz parte deste catálogo e não foi alterado.
