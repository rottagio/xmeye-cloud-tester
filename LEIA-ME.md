# XMEye Cloud Tester — login por QR

Aplicativo portátil Windows x64 para vincular uma conta XMEye/iCSee, obter as
câmeras associadas e abrir o vídeo remotamente pelo ecossistema cloud Xiongmai.

## Fluxo

`QR oficial → conta cloud → lista de dispositivos → Cloud ID → CMS/CloudSN → vídeo`

O QR segue o fluxo atual do VMS Pro e usa chamadas HTTPS assinadas pelo
`CMSClient.dll`. Não é necessário digitar e-mail, senha ou CAPTCHA no Windows.
Tokens e credenciais internas não são persistidos pelo aplicativo.

Consulte `COMO_TESTAR.txt` antes do primeiro uso.
