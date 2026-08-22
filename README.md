# XMEye Cloud Tester

Aplicativo portatil para Windows em desenvolvimento para visualizar dispositivos
iCSee/XMEye vinculados a uma conta cloud.

## Atualizacoes

O executavel inicial verifica a release mais recente no GitHub. O arquivo de release
`XMEyeCloudTester-update.zip` contem somente os dois modulos gerenciados do projeto:

- `XMEyeCloudTester.dll`
- `XMEyeCloudTester.App.dll`

As bibliotecas oficiais da fabricante nao sao publicadas neste repositorio. Elas
permanecem na instalacao original do usuario.

Ao publicar uma tag no formato `vX.Y.Z`, o GitHub Actions compila os dois projetos,
cria o pacote incremental e publica uma Release automaticamente. O aplicativo
compara essa versao com a versao instalada, pede confirmacao e aplica os arquivos
somente depois de encerrar o processo atual.

O primeiro pacote portatil ainda precisa ser obtido manualmente porque contem o
runtime e as bibliotecas oficiais. Depois disso, as atualizacoes do nosso codigo
sao feitas pelo proprio aplicativo.

## Privacidade

Credenciais de conta e de dispositivo nao devem ser gravadas no codigo, no Git ou
nos diagnosticos. O aplicativo mantem senhas apenas em memoria durante a sessao.
