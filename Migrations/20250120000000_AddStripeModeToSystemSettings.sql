-- Add StripeMode columns to SystemSettings table
-- Migration: 20250120000000_AddStripeModeToSystemSettings

-- Add StripeMode column
ALTER TABLE "SystemSettings" 
ADD COLUMN "StripeMode" character varying(20) NOT NULL DEFAULT 'production';

-- Add StripeModeChangedAt column
ALTER TABLE "SystemSettings" 
ADD COLUMN "StripeModeChangedAt" timestamp with time zone NULL;

-- Add StripeModeChangedByUserId column
ALTER TABLE "SystemSettings" 
ADD COLUMN "StripeModeChangedByUserId" integer NULL;






