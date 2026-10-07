# Preserve the user's interactive startup configuration, then report the local directory.
if [[ -f ~/.bashrc ]]; then source ~/.bashrc; fi
__lwt_report_directory() {
    local LC_ALL=C text="$PWD" encoded='' char index byte
    for ((index=0; index<${#text}; index++)); do
        char="${text:index:1}"
        case "$char" in
            [a-zA-Z0-9/._~-]) encoded+="$char" ;;
            *) printf -v byte '%d' "'$char"; printf -v char '%%%02X' "$((byte & 255))"; encoded+="$char" ;;
        esac
    done
    printf '\033]7;file://%s\007' "$encoded"
    if [[ -z "$(jobs -pr)" ]]; then printf '\033]133;A\007'; fi
}
if declare -p PROMPT_COMMAND 2>/dev/null | grep -q 'declare -a'; then
    PROMPT_COMMAND+=(__lwt_report_directory)
else
    PROMPT_COMMAND="${PROMPT_COMMAND:+$PROMPT_COMMAND; }__lwt_report_directory"
fi
