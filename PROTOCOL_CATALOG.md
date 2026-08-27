# Catálogo técnico VMS Pro / FunSDK

Este levantamento descreve o protocolo, não uma conta ou modelos específicos. Nenhum identificador, IP, usuário ou senha observado nos logs foi copiado para o código.

## Configuração básica tipada (VMS Pro)

A tela de configuração básica usa `CMS_Client_GetDeviceConfig_V2` e
`CMS_Client_SetDeviceConfig_V2`, as mesmas rotas chamadas pelos wrappers
síncronos do `CGlobalLogic` no VMS Pro. A interface recebe os bytes do buffer
do callback; `netsdk.log` não é fonte de dados.

| Bloco | Seletor de leitura | Seletor de escrita | Tamanho | Campos usados |
|---|---:|---:|---:|---|
| `GENERAL_CONFIG_BASE` | `0x103ED` | `0x103ED` | `0x5C` | `sMachineName` em `0x0C` |
| `LOCATION_CONFIG_BASE` | `0x103EE` | — | `0x70` | `szLanguage` em `0x08` (consulta) |
| `SDK_CameraParam` | `0x5E` | `0x5E` | `0x54` | flip `0x28`, mirror `0x2C`, DNC `0x3C` |
| `ALL_VIDEO_VOLUME` | `0x1F8` | `0x1F8` | `0x1404` | saída em `0xA04`, volumes em `0xA24/0xA28` |
| `SDK_TimeZone` | `0xA5` | `0xA5` | `0x08` | minutos a oeste em `0x00` |
| `SDK_SYSTEM_TIME` | `0x103F3` | `0x2D` | `0x20` | oito inteiros de data/hora |

Os layouts, tamanhos e seletores acima foram confirmados no PDB e no
disassembly do `ConfigModule.dll` distribuído com o VMS Pro. Toda escrita parte
de uma cópia integral do bloco lido, muda apenas os offsets listados e só é
considerada concluída depois de uma nova leitura compatível.

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

## Camada de leitura segura

`DeviceConfigurationReadPolicy` é a porta única das leituras remotas:

- `SystemFunction` é a única descoberta automática permitida; ocorre uma vez por dispositivo/firmware, depois da primeira imagem confirmada e sem repetição automática.
- Armazenamento, gravação e iluminação continuam sob demanda e passam pela mesma fila serial já existente.
- Uma definição sem comando de leitura, uma operação ou uma ação destrutiva é recusada antes de chegar ao SDK.
- Se `SystemFunction` negar explicitamente uma capacidade, sua leitura detalhada também é recusada.
- Respostas válidas são gravadas como evidência no perfil individual; timeouts e respostas inválidas não viram “incompatível”.
- O cache local existente é reaproveitado no início, sem consultar novamente a câmera.

## Apresentação por câmera

A tela **Configurações da câmera** é montada a partir do perfil do dispositivo selecionado:

- separa os itens por seção e permite filtrar uma seção;
- mostra como disponível somente o que foi confirmado para aquela câmera e firmware;
- omite recursos incompatíveis ou ainda desconhecidos dos controles utilizáveis;
- mantém visíveis apenas as sondagens seguras de armazenamento e gravação;
- informa a quantidade de recursos confirmados, incompatíveis e ainda não identificados;
- identifica recursos de leitura/gravação; somente editores liberados explicitamente pela política podem alterar o dispositivo.

## Escrita controlada da versão 0.16

Os editores do menu usam uma lista positiva por configuração. Cada editor só é habilitado quando a câmera devolve o bloco e o campo correspondente em uma leitura válida.

Fluxo obrigatório:

1. câmera com imagem online e fora de cooldown;
2. leitura fresca do bloco completo;
3. validação de tipo e intervalo do campo alterado;
4. confirmação adicional para alterações sensíveis, como a troca de Wi-Fi;
5. uma única gravação pelo comando confirmado do VMS Pro;
6. nova leitura após a gravação;
7. sucesso somente se o dispositivo devolver o valor solicitado;
8. se devolver outro valor, uma única restauração do bloco original e uma leitura de confirmação;
9. se não houver resposta, nenhuma repetição ou restauração incerta é enviada.

Há intervalo mínimo persistente entre alterações do mesmo bloco. Wi-Fi exige uma confirmação adicional; firmware e formatação continuam fora do fluxo comum por serem operações destrutivas.

## Interface de configurações da versão 0.16

O botão **Configurações da câmera** abre uma interface voltada ao cliente, não a tabela técnica:

- menu lateral com categorias;
- cartões no mesmo tema visual do monitor;
- textos simples, sem nomes de protocolo, evidências ou códigos internos;
- botões somente para ações realmente implementadas;
- as categorias do aplicativo móvel permanecem estáveis e campos ausentes no firmware aparecem desabilitados;
- nome, imagem, gravação, detecção, rastreamento, luz, áudio, armazenamento, hora e Wi-Fi usam os valores realmente devolvidos pelo aparelho;
- WeChat, não perturbe e preferências de notificação do sistema são identificados como funções do celular, não como configurações da câmera.

A tabela de compatibilidade continua separada como ferramenta de diagnóstico e não é usada para configurar a câmera.

### Fluxo confirmado no VMS Pro

Os logs instrumentados do VMS Pro confirmam que a configuração é carregada por dispositivo e por seção:

1. ao obter o contexto do dispositivo, consulta `SystemInfo` (`1020`) e `SystemFunction` (`1360`);
2. as páginas pedem somente os blocos necessários com a leitura JSON genérica `1042`;
3. o canal é `-1` para dados do dispositivo e `0..N` para configurações por canal;
4. uma alteração envia o bloco validado com `1040` e precisa ser relida antes de a interface confirmar o novo valor.

Na captura instrumentada, o VMS consultou sob demanda `General.General`, `General.Location`,
`System.TimeZone`, `OPTimeQuery`, `Detect.HumanDetection`, `Camera.WhiteLight`,
`fVideo.Volume`, `fVideo.VolumeIn` e `Uart.PTZControlCmd`. O VMS de desktop não apresenta
todas as páginas específicas do aplicativo móvel; por isso a organização do menu segue a
experiência móvel, enquanto os nomes de comando e payloads precisam continuar comprovados
individualmente no VMS/FunSDK antes de liberar cada escrita.

O menu comum não deve expor ferramentas técnicas como RTSP, atualização de firmware ou
posições PTZ. Essas funções pertencem a áreas próprias do produto ou permanecem protegidas.
