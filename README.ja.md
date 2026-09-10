<h1 align="center">Jellyfin Plugin MetaTube</h1>
<p align="center"><b><a href="README.md">English</a> | 日本語</b></p>

<p align="center">
<img alt="Plugin Banner" src="https://metatube-community.github.io/images/banner-dark.png"/>
<br/>
<br/>

<a href="https://github.com/metatube-community/jellyfin-plugin-metatube/actions">
<img alt="GitHub Workflow Status" src="https://img.shields.io/github/actions/workflow/status/metatube-community/jellyfin-plugin-metatube/dotnetcore.yml?branch=main&logo=github">
</a>
<a href="https://github.com/metatube-community/jellyfin-plugin-metatube/search?l=c%23">
<img alt="GitHub top language" src="https://img.shields.io/github/languages/top/metatube-community/jellyfin-plugin-metatube?color=%23239120&label=.NET&logo=csharp">
</a>
<a href="https://github.com/metatube-community/jellyfin-plugin-metatube/blob/main/LICENSE">
<img alt="License" src="https://img.shields.io/github/license/metatube-community/jellyfin-plugin-metatube">
</a>
<a href="https://github.com/metatube-community/jellyfin-plugin-metatube">
<img alt="gitHub Stars" src="https://img.shields.io/github/stars/metatube-community/jellyfin-plugin-metatube?style=flat">
</a>
<a href="https://github.com/metatube-community/jellyfin-plugin-metatube">
<img alt="Downloads" src="https://img.shields.io/github/downloads/metatube-community/jellyfin-plugin-metatube/total">
</a>
<a href="https://github.com/metatube-community/jellyfin-plugin-metatube/releases">
<img alt="Releases" src="https://img.shields.io/github/v/release/metatube-community/jellyfin-plugin-metatube?include_prereleases&logo=smartthings">
</a>
</p>

## 概要

Jellyfin／Emby 向けに開発された、とても便利なメタデータプラグインです。

## 特徴

- 完全なデータ：タイトル、概要、出演者、タグ、評価 などを含む豊富な情報を提供。
- 強力な検索機能：多数のスクレイピングソースから作品や俳優情報を検索可能。
- トレーラー機能：動画をダウンロードせずに オンラインで予告編を視聴。
- スケジュールタスク：自動的に作品タグを整理し、バックグラウンドでプラグインを更新。
- 顔認識機能：内蔵の顔認識により、顔を中心にポスター画像を自動トリミング。
- 自動翻訳：特定のメタデータ内容を必要な言語に翻訳可能。

## 対応プラットフォーム

[![Jellyfin](https://img.shields.io/static/v1?color=%2300A4DC&style=for-the-badge&label=Jellyfin&logo=jellyfin&message=12.0)](https://jellyfin.org/)
[![Emby](https://img.shields.io/static/v1?color=%2352B54B&style=for-the-badge&label=Emby&logo=emby&message=4.9.x)](https://emby.media/)

_※本プロジェクトは Jellyfin／Emby の安定版のみをサポートしています。_

新しい Jellyfin ビルドは **Jellyfin 12.0 / .NET 10** を対象とします。Jellyfin 10.11 用の既存パッケージはカタログに残しますが、新規ビルドは作成しません。Emby は引き続き **4.9.x / .NET 8** を対象とします。

### ビルドと検証

.NET 10 SDK と Python 3.12 以降をインストールしてください。CI では Emby 用に .NET 8 もインストールします。リポジトリのルートで実行します。

```sh
dotnet build Jellyfin.Plugin.MetaTube/Jellyfin.Plugin.MetaTube.csproj -c Release
dotnet build Jellyfin.Plugin.MetaTube/Jellyfin.Plugin.MetaTube.csproj -c Release.Emby
dotnet test tests/MetaTube.Tests/MetaTube.Tests.csproj -c Release
python -m unittest discover -s scripts/tests -v
```

ZIP は `Jellyfin.Plugin.MetaTube/bin` に生成されます。回帰テストはローカルの模擬バックエンドを使用し、実際の認証情報は不要です。ビルド成功とサーバー上の動作確認は別です。[リリース手順](docs/RELEASING.md)を参照してください。

### Jellyfin 10.11 からの更新

1. Jellyfin を停止し、MetaTube の設定を含むデータ・設定ディレクトリ全体をバックアップします。
2. 古い MetaTube のプラグインバイナリを削除します。`MetaTube.xml` と設定は保持してください。
3. Jellyfin 12.0 に更新し、データベース移行の完了後、必須のライブラリ全体スキャンを実行します。
4. 動作確認済みの Jellyfin 12.0 用 MetaTube をインストールして再起動し、設定・メタデータ・画像・定期タスクを確認します。

Jellyfin を元に戻す場合は、更新前の完全なバックアップを復元する必要があります。DLL の差し替えだけではデータベースを元に戻せません。事前に [Jellyfin の更新ガイド](https://jellyfin.org/posts/jellyfin-release-12.0/)を確認してください。

## ドキュメント

- [プラグインのインストール](https://metatube-community.github.io/wiki/plugin-installation/)
- [バックエンドのデプロイ](https://metatube-community.github.io/wiki/server-deployment/)
- [命名規則](https://metatube-community.github.io/wiki/naming-rules/)
- [自動翻訳](https://metatube-community.github.io/wiki/auto-translation/)
- [ソースからのビルド](https://metatube-community.github.io/wiki/build-from-source/)
- [データソース](https://metatube-community.github.io/wiki/metadata-providers/)

詳細な使い方や解説は [Wiki](https://metatube-community.github.io/wiki/) をご参照ください。

## コミュニティ

質問や提案などは、[Discussions](https://github.com/metatube-community/jellyfin-plugin-metatube/discussions) にてお気軽にどうぞ。

## ライセンス

本プラグインは [MIT](https://github.com/metatube-community/jellyfin-plugin-metatube/blob/main/LICENSE) ライセンスの下で公開されています。

## スター履歴

[![Star History Chart](https://api.star-history.com/svg?repos=metatube-community/jellyfin-plugin-metatube&type=Date)](https://star-history.com/#metatube-community/jellyfin-plugin-metatube&Date)
