{{/* Chart name. */}}
{{- define "bayios.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/* Release-scoped base name. */}}
{{- define "bayios.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{- define "bayios.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/* Common labels. Call with (dict "ctx" $ "component" "api"). */}}
{{- define "bayios.labels" -}}
helm.sh/chart: {{ include "bayios.chart" .ctx }}
{{ include "bayios.selectorLabels" . }}
app.kubernetes.io/version: {{ .ctx.Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .ctx.Release.Service }}
app.kubernetes.io/part-of: bayios
{{- end }}

{{- define "bayios.selectorLabels" -}}
app.kubernetes.io/name: {{ include "bayios.name" .ctx }}
app.kubernetes.io/instance: {{ .ctx.Release.Name }}
app.kubernetes.io/component: {{ .component }}
{{- end }}

{{/* image reference: (dict "ctx" $ "image" .Values.api.image) */}}
{{- define "bayios.image" -}}
{{- printf "%s:%s" .image.repository (default .ctx.Chart.AppVersion .image.tag) }}
{{- end }}

{{- define "bayios.api.secretName" -}}
{{- if .Values.api.secret.existingSecret }}{{ .Values.api.secret.existingSecret }}{{ else }}{{ include "bayios.fullname" . }}-api{{ end }}
{{- end }}

{{- define "bayios.postgres.fullname" -}}
{{ include "bayios.fullname" . }}-postgres
{{- end }}

{{- define "bayios.postgres.secretName" -}}
{{- if .Values.postgres.auth.existingSecret }}{{ .Values.postgres.auth.existingSecret }}{{ else }}{{ include "bayios.postgres.fullname" . }}{{ end }}
{{- end }}

{{/* Pod security context: global defaults merged with the component's own (component wins). */}}
{{- define "bayios.podSecurityContext" -}}
{{- toYaml (merge (deepCopy .component) (deepCopy .ctx.Values.podSecurityContext)) }}
{{- end }}
