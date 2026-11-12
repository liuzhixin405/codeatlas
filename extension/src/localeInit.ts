// 必须是 extension.ts 的第一个 import：其余模块里的模块级文案常量在加载时就按语言求值。
import * as vscode from "vscode";
import { setLocale } from "../webview/l10n";

setLocale(vscode.env.language);
