import * as path from 'path';
import * as vscode from 'vscode';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
    TransportKind,
} from 'vscode-languageclient/node';

let client: LanguageClient | undefined;

export function activate(context: vscode.ExtensionContext): void {
    const config = vscode.workspace.getConfiguration('aura');

    if (!config.get<boolean>('lsp.enabled', true)) {
        return;
    }

    const compilerPath = config.get<string>('compiler.path', '');

    // Determine how to launch the LSP server
    let serverOptions: ServerOptions;

    if (compilerPath) {
        // Use dotnet run with the specified project path
        serverOptions = {
            command: 'dotnet',
            args: ['run', '--project', compilerPath, '--', 'lsp'],
            transport: TransportKind.stdio,
        };
    } else {
        // Assume 'aura' is on PATH (published executable)
        serverOptions = {
            command: 'aura',
            args: ['lsp'],
            transport: TransportKind.stdio,
        };
    }

    const clientOptions: LanguageClientOptions = {
        documentSelector: [{ scheme: 'file', language: 'aura' }],
        synchronize: {
            fileEvents: vscode.workspace.createFileSystemWatcher('**/*.aura'),
        },
        outputChannelName: 'Aura Language Server',
    };

    // Configure trace level
    const trace = config.get<string>('lsp.trace', 'off');
    if (trace !== 'off') {
        clientOptions.traceOutputChannel = vscode.window.createOutputChannel(
            'Aura Language Server Trace'
        );
    }

    client = new LanguageClient(
        'aura',
        'Aura Language Server',
        serverOptions,
        clientOptions
    );

    client.start();
}

export function deactivate(): Thenable<void> | undefined {
    if (!client) {
        return undefined;
    }
    return client.stop();
}
