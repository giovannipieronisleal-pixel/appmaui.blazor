#!/usr/bin/env bash
# =============================================================================
# Clothing Store - Script de Deploy no Google Cloud Run (Linux / Cloud Shell)
# =============================================================================
set -e

SERVICE_NAME="clothingstore-catalog"
REGION="southamerica-east1"

echo "============================================================================="
echo "  CLOTHING STORE - DEPLOY DO SITE NO GOOGLE CLOUD RUN (24H ONLINE)"
echo "============================================================================="
echo ""

echo "Publicando no Google Cloud Run..."
gcloud run deploy "$SERVICE_NAME" \
    --source . \
    --platform managed \
    --region "$REGION" \
    --allow-unauthenticated \
    --port 8080 \
    --min-instances 0 \
    --max-instances 2 \
    --memory 512Mi

echo ""
echo "Deploy finalizado! Copie a URL HTTPS gerada acima e cole nas Configurações da Loja no aplicativo."

