---
name: Bug report
about: Report an issue, error, bug, or incorrect behavior.
description: Report an issue, error, bug, or incorrect behavior.
title: "[BUG] "
labels: ["bug"]
assignees:
  - Unknown6656
body:
  - type: markdown
    attributes:
      value: Thanks for taking the time to fill out this bug report!
  - type: textarea
    id: bug-summary
    attributes:
      label: Bug description
      description: A clear and concise description of what the bug is. Include a stack trace, console output, debug dump, and/or screenshots.
      placeholder: What happened?
    validation:
      required: true
  - type: textarea
    id: reproduce-steps
    attributes:
      label: Steps to reporoduce
      description: A list describing on how to reproduce the behavior.
      placeholder: |
        1. Type '...'
        2. Execute '....'
        3. Scroll down to '....'
        4. See error
    validation:
      required: true
  - type: textarea
    id: expected-behavior
    attributes:
      label: Expected behavior
      description: Instead of the behavior written above, what would you have expected the AutoIt interpreter to do?
    validation:
      required: false
  - type: dropdown
    id: os-type
    attributes:
      label: Operating System Type
      multiple: false
      options:
        - Windows
        - MacOS
        - Linux
        - Other POSIX compliant OS
        - Other non-POSIX compliant OS
  - type: input
    id: os-name
    attributes:
      label: Operating System Descriptor
      description: Paste the result of executing `ver` (Windows) or `uname -a` (Unix) here.
      placeholder: "Microsoft Windows [Version 10.0.22631.3296]"
    validations:
      required: true
  - type: textarea
    id: interpreter-version
    attributes:
      label: AutoIt Interpreter Version
      description: |
        Paste the result of executing `./autoit3 --version` here, e.g.:
        ```
        +-----------------+
        |        **+o.    |
        |       .=*.+     |   AUTOIT3 INTERPRETER
        |       . ++ o    |     Written by Unknown6656, 2018 - 2024
        |        ...o     |
        |       +S E      |   https://github.com/Unknown6656/AutoIt-Interpreter/
        |    . +.o. +.    |
        |     B o.*.o+    |   Version 0.12.2274.8640, a555aba17d74e5df3628eb44edcbe54d9397f583
        |    + * =.Oo..   |   B5CF99E91A99B93C2EA925B24ED78D670267ABF5453103429E7064F30C94DEB6
        |     +o=o*.o.    |
        +-----------------+
        ```
      placeholder:
      render: text
    validation:
      required: true
  - type: textarea
    id: additional-info
    attributes:
      label: Additional Info
      description: Additional information you'd like to provide.
      placeholder: Did we miss anything?
    validation:
      required: false
---

### Bug description


### Steps to reproduce
Steps to reproduce the behavior:
1. Type '...'
2. Execute '....'
3. Scroll down to '....'
4. See error

### Expected behaviour
A clear and concise description of what you expected to happen.

### Stack Trace or Debug Log
If applicable, insert the stack trace in a code block here.

### Screenshots
If applicable, add screenshots to help explain your problem.

### AutoIt Interpreter version
Paste the output of the command `./autoit3 --version` here, e.g.:


### Environment
 - OS and Architecture: _[e.g. Windows 10 64Bit, Build № 42] (this can be obtained using the command `ver` on windows or `uname -a` on linux)_
- _insert additional configuration information if needed_

<!-- optional -->
### Additional context
Add any other context about the problem here.
