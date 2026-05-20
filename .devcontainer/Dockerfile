FROM mcr.microsoft.com/dotnet/sdk:10.0

ARG USERNAME=vscode
ARG USER_UID=1000
ARG USER_GID=$USER_UID

RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        wget \
        gnupg \
        less \
        jq \
        unzip \
        procps \
    && rm -rf /var/lib/apt/lists/*

# Non-root user. The .NET 10 SDK image ships with a UID-1000 `app` user;
# rename it to `vscode` so we have a stable, predictable account.
RUN if getent passwd $USER_UID > /dev/null; then \
        existing_user=$(getent passwd $USER_UID | cut -d: -f1); \
        existing_group=$(getent group $USER_GID | cut -d: -f1); \
        usermod -l $USERNAME -d /home/$USERNAME -m -s /bin/bash $existing_user; \
        if [ -n "$existing_group" ] && [ "$existing_group" != "$USERNAME" ]; then \
            groupmod -n $USERNAME $existing_group; \
        fi; \
    else \
        groupadd --gid $USER_GID $USERNAME && \
        useradd --uid $USER_UID --gid $USER_GID -m -s /bin/bash $USERNAME; \
    fi

# Persistent bash history across rebuilds (matches the mount in devcontainer.json).
RUN mkdir -p /commandhistory \
    && touch /commandhistory/.bash_history \
    && chown -R $USER_UID:$USER_GID /commandhistory \
    && echo 'export HISTFILE=/commandhistory/.bash_history' >> /home/$USERNAME/.bashrc \
    && echo 'export PROMPT_COMMAND="history -a; $PROMPT_COMMAND"' >> /home/$USERNAME/.bashrc

USER $USERNAME
