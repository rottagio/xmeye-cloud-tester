# iCSee/XMEye Monitor para Windows

Aplicativo portátil Windows x64 para visualizar remotamente câmeras compatíveis
com iCSee/XMEye. O fluxo de conta usa o login oficial do VMS Pro e o vídeo usa o
motor CMS/Cloud P2P da Xiongmai.

## Recursos atuais

- login da conta por QR, e-mail/usuário ou restauração protegida da sessão;
- importação das câmeras vinculadas à conta e atualização periódica dos nomes;
- cadastro individual por QR da câmera, Cloud ID ou IP/porta;
- grades de 1, 4, 9 e 16 quadros, com ordem e último layout persistidos;
- detecção dos canais que realmente entregam imagem;
- estados Online, Offline e Reconectando, com recuperação automática;
- qualidade SD/HD por câmera e indicação P2P ou rede local;
- áudio, fala, PTZ/zoom, captura, gravação local, rotação, espelhamento local e
  janela separada, conforme o recurso suportado pelo dispositivo;
- painel PTZ fixo com direções corrigidas, posições favoritas e controles rápidos
  sobre cada vídeo;
- barra responsiva com ações agrupadas e botões SD/HD, inclusive em janelas
  menores;
- preferências por câmera para rastreamento, sensibilidade, tempo/posição,
  detecção de pessoa, rastros e avisos. O envio remoto só é confirmado quando o
  firmware expõe uma interface compatível;
- biblioteca separada de vídeos e imagens, com miniaturas, reprodutor e exclusão
  recuperável pela Lixeira;
- grupos/ambientes, apelidos locais, ordem, ocultação e serial mascarado;
- tema claro/escuro, português/inglês, inicialização com o Windows, pastas e
  limite de armazenamento configuráveis;
- diagnóstico exportável sem credenciais.

## Primeiro uso

1. Execute `XMEyeCloudTester.exe` no pacote portátil completo.
2. Leia o QR com XMEye/iCSee no celular e confirme o vínculo, ou use o login de
   conta na tela **Câmeras**.
3. Abra **Ao vivo** e escolha a grade. O aplicativo testa os dispositivos em
   sequência e só reserva quadros para canais confirmados ou em reconexão.
4. Organize nomes, ambientes, ordem e qualidade na tela **Câmeras**.

Não mova somente o executável: as DLLs oficiais e os plugins Qt incluídos no
pacote precisam permanecer ao lado dele.

## Privacidade

- senhas de conta ou de dispositivo nunca são escritas no código nem no log;
- senhas digitadas para dispositivos manuais permanecem somente em memória;
- a sessão de conta restaurável é protegida pelo Windows para o usuário atual;
- serial, e-mail e identificadores são mascarados nos diagnósticos;
- **Sair da conta** remove a sessão protegida deste computador.

Dados locais ficam em `%LOCALAPPDATA%\XMEyeCloudAccountTester`. Fotos e vídeos
usam as pastas escolhidas em **Configurações**.

## Diagnóstico

Abra **Configurações > Diagnóstico técnico > Exportar diagnóstico**. Os códigos
do SDK são acompanhados por mensagens compreensíveis; o arquivo exportado não
inclui senhas ou tokens.

## Atualizações

O pacote inicial completo é instalado uma vez. Depois, o atualizador consulta a
release mais recente de `rottagio/xmeye-cloud-tester`, encerra o aplicativo,
substitui os módulos e reabre a mesma instalação. Releases são publicadas com
tags `vX.Y.Z`; o artefato incremental chama-se `XMEyeCloudTester-update.zip`.

As bibliotecas oficiais proprietárias não são armazenadas no histórico Git.
