# XMEye Cloud Tester — conta cloud

Aplicativo portátil Windows x64 para entrar com uma conta XMEye/iCSee, obter as
câmeras vinculadas e abrir o vídeo remotamente pelo ecossistema cloud Xiongmai.

## Fluxo

`conta cloud → lista de dispositivos → Cloud ID → CMS/CloudSN → vídeo`

O aplicativo usa a API HTTPS atual de `api.xmeye.net` para autenticação/listagem
e `CMSClient.dll` para conexão P2P e reprodução. O login exige a imagem CAPTCHA
fornecida pelo serviço. Nenhuma senha ou token é persistido pelo código do aplicativo.

Consulte `COMO_TESTAR.txt` antes do primeiro uso.
