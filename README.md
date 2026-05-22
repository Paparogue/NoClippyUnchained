# NoClippyUnchained
![NoClippyUnchained Logo](https://raw.githubusercontent.com/Paparogue/NoClippyUnchained/master/noclippyunchained.png)

A Dalamud plugin for FFXIV that improves gameplay responsiveness by fully removing animation locks.

## Features
- **Animation Lock Compensation**: Reduces animation lock delays for a smoother gameplay experience
- **Percentage-based Reduction**: Configure animation lock reduction as a percentage instead of fixed values

## Installation
Add the following URL to your third-party repository list in the Dalamud Plugin Installer:
```
https://raw.githubusercontent.com/Paparogue/PaparogueRepo/refs/heads/main/repo.json
```

## Configuration Guide

![NoClippyUnchained UI](https://raw.github.com/Paparogue/NoClippyUnchained/c9c81114ad65a4fe22fea29954b0fc452d8570fb/grafik.png)

### General Settings
- **Enable/Disable Animation Lock Reduction**: Toggle the main functionality on or off
- **Use Percentage Instead of Fixed Reduction**: Switch between fixed 1ms reduction or percentage-based

### Advanced Options
- **Animation Lock Reduction Percentage**: When using percentage mode, set reduction between 1% and 100%
- **Allow Cast & Limit Break Animation Lock Reduction**: Optionally include these action types
- **Enable Logging**: View detailed information about every action and its adjustment
- **Dry Run**: Test the system without modifying gameplay (helpful for evaluating impact)

### Statistics
- Track total time saved across all actions
- Monitor the number of actions that have been optimized

## Commands
- `/noclippyunchained`: Opens the configuration window
- `/noclippyunchained on`: Enables animation lock compensation
- `/noclippyunchained off`: Disables animation lock compensation
- `/noclippyunchained toggle` or `/noclippyunchained t`: Toggles animation lock compensation
- `/noclippyunchained dry` or `/noclippyunchained d`: Toggles dry run mode
