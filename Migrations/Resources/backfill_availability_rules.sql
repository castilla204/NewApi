-- Backfill idempotente: ExpertAvailability (rango único + DaysOfWeek JSON) -> ExpertAvailabilityRule
-- (una fila por (dia, rango)). Ejecutar A MANO en dev/prod tras aplicar la migración
-- 20260616195633_AddExpertAvailabilityRuleAndAppointmentSlots.
--
-- Mapea nombres EN de DaysOfWeek a System.DayOfWeek (0=Sunday..6=Saturday).
-- Solo inserta para expertos que aún no tengan reglas (idempotente).

INSERT INTO "ExpertAvailabilityRules"
    ("ExpertId","DayOfWeek","StartLocal","EndLocal","Timezone","EffectiveFrom","EffectiveTo","IsActive","CreatedAt","UpdatedAt")
SELECT ea."ExpertId",
       CASE d.day
         WHEN 'Sunday'    THEN 0
         WHEN 'Monday'    THEN 1
         WHEN 'Tuesday'   THEN 2
         WHEN 'Wednesday' THEN 3
         WHEN 'Thursday'  THEN 4
         WHEN 'Friday'    THEN 5
         WHEN 'Saturday'  THEN 6
       END AS dow,
       ea."StartTime", ea."EndTime", ea."Timezone",
       ea."EffectiveFrom", ea."EffectiveTo", ea."IsActive",
       CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
FROM "ExpertAvailabilities" ea
CROSS JOIN LATERAL jsonb_array_elements_text(ea."DaysOfWeek"::jsonb) AS d(day)
WHERE ea."IsActive" = true
  -- Salta valores de día no mapeables (evita DayOfWeek NULL -> abort del INSERT entero).
  AND d.day IN ('Sunday','Monday','Tuesday','Wednesday','Thursday','Friday','Saturday')
  AND NOT EXISTS (
      SELECT 1 FROM "ExpertAvailabilityRules" r WHERE r."ExpertId" = ea."ExpertId"
  );
