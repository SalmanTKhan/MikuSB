#!/usr/bin/env python3
"""Create a machine-local configuration without changing the shared template."""
import argparse
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
LOCAL = ROOT / 'Config' / 'Config.local.json'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--resources', help='directory containing the game resource data')
    parser.add_argument('--game', help='Windows Game.exe (optional for server-only setup)')
    parser.add_argument('--force', action='store_true', help='replace existing local configuration')
    args = parser.parse_args()
    if LOCAL.exists() and not args.force:
        parser.error(f'{LOCAL} already exists; edit it or pass --force to replace it')
    resources = args.resources or input('Resource data directory: ').strip().strip('"')
    resource_path = Path(resources).expanduser().resolve()
    if not resources or not resource_path.is_dir():
        parser.error('resource directory does not exist')
    game = args.game
    if game is None and sys.platform == 'win32':
        game = input('Path to Game.exe (leave blank for server only): ').strip().strip('"')
    game_path = Path(game).expanduser().resolve() if game else None
    if game_path and not game_path.is_file():
        parser.error('game executable does not exist')
    with (ROOT / 'Config' / 'Config.json').open(encoding='utf-8-sig') as f:
        config = json.load(f)
    config['Path']['ResourcePath'] = str(resource_path)
    config['Loader']['GamePath'] = str(game_path) if game_path else ''
    config['Loader']['Arguments'] = [a for a in config['Loader'].get('Arguments', [])
                                      if not a.lower().startswith('-userdir=')]
    config['Loader']['EnableInGameConsole'] = False
    config['Loader']['AutoUpdateInGameConsole'] = False
    LOCAL.write_text(json.dumps(config, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    print(f'Created {LOCAL} (ignored by Git).')
    if not shutil.which('dotnet'):
        print('Install the .NET 10 SDK before starting the server.')
    print('Start the server with start-server.bat on Windows or ./start-server.sh on Linux/macOS.')
    if game_path:
        print('Windows client launch also requires a locally built runtime patch; see docs/setup.md.')


if __name__ == '__main__':
    main()
