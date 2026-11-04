{{/* Chart name */}}
{{- define "whiskers.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* Fully qualified app name */}}
{{- define "whiskers.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/* Common labels */}}
{{- define "whiskers.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{ include "whiskers.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{/* Selector labels */}}
{{- define "whiskers.selectorLabels" -}}
app.kubernetes.io/name: {{ include "whiskers.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{/* ServiceAccount name */}}
{{- define "whiskers.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "whiskers.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{/* Image reference: tag defaults to the chart appVersion */}}
{{- define "whiskers.image" -}}
{{- printf "%s:%s" .Values.image.repository (default .Chart.AppVersion .Values.image.tag) -}}
{{- end -}}

{{/*
R1-46: values the chart refuses to render (fail closed) instead of producing a pod that cannot start or that
starts in an unintended mode.
*/}}
{{- define "whiskers.validate" -}}
{{- if eq (lower (toString .Values.image.tag)) "latest" -}}
{{- fail "image.tag \"latest\" is refused: leave it empty (the chart's release) or name a release" -}}
{{- end -}}
{{- if not (has .Values.database.provider (list "sqlite" "postgres")) -}}
{{- fail (printf "database.provider must be sqlite or postgres, not %q" (toString .Values.database.provider)) -}}
{{- end -}}
{{- if and (eq .Values.database.provider "postgres") (not .Values.database.existingSecret) (not .Values.database.connectionString) -}}
{{- fail "database.provider=postgres needs database.existingSecret (a Secret with key connectionString) or database.connectionString" -}}
{{- end -}}
{{- if .Values.tailscaleSidecar.enabled -}}
{{- $img := toString .Values.tailscaleSidecar.image -}}
{{- if not (regexMatch "^[^@\\s]+(:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?(@sha256:[0-9a-f]{64})?$" $img) -}}
{{- fail (printf "tailscaleSidecar.image %q is not a valid image reference" $img) -}}
{{- end -}}
{{- $digest := regexMatch "@sha256:[0-9a-f]{64}$" $img -}}
{{- $name := regexReplaceAll "@sha256:[0-9a-f]{64}$" $img "" -}}
{{- $last := regexReplaceAll "^.*/" $name "" -}}
{{- $tag := "" -}}
{{- if contains ":" $last -}}{{- $tag = regexReplaceAll "^.*:" $last "" -}}{{- end -}}
{{- if or (eq (lower $tag) "latest") (and (not $digest) (eq $tag "")) -}}
{{- fail (printf "tailscaleSidecar.image %q is not pinned: name a release tag (not latest) and/or a digest" $img) -}}
{{- end -}}
{{- end -}}
{{- $fixed := list "vpn_provider" "vpn__provider" "tailscale_authkey" "vpn_force_legacy_tailscale" -}}
{{- range .Values.extraEnv -}}
{{- /* .NET reads "Vpn:Provider" as the same key as "Vpn__Provider" (and Kubernetes accepts ":" in env names). */ -}}
{{- $n := regexReplaceAll ":" (lower (toString .name)) "__" -}}
{{- $bare := regexReplaceAll "^(dotnet_|aspnetcore_)" $n "" -}}
{{- if has $bare $fixed -}}
{{- fail (printf "extraEnv %q is refused: the chart fixes the VPN mode (VPN_PROVIDER=none, Vpn__Provider=none; see values.yaml)" (toString .name)) -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/* PVC holding the Tailscale sidecar's node state */}}
{{- define "whiskers.tailscaleStateClaim" -}}
{{- .Values.tailscaleSidecar.state.existingClaim | default (printf "%s-tailscale" (include "whiskers.fullname" .)) -}}
{{- end -}}
