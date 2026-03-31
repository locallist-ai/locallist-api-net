// Quick test to verify R2 credentials work
const { S3Client, ListObjectsV2Command } = require('@aws-sdk/client-s3');

const R2_ACCOUNT_ID = process.env.R2_ACCOUNT_ID || 'da270186da32e41c7443ad387683733f';

const s3 = new S3Client({
    region: 'auto',
    endpoint: `https://${R2_ACCOUNT_ID}.r2.cloudflarestorage.com`,
    credentials: {
        accessKeyId: process.env.R2_ACCESS_KEY_ID,
        secretAccessKey: process.env.R2_SECRET_ACCESS_KEY,
    },
});

s3.send(new ListObjectsV2Command({ Bucket: 'locallist-images', MaxKeys: 3, Prefix: 'places/' }))
    .then(r => {
        console.log('R2 connection OK');
        console.log('Sample keys:', (r.Contents || []).map(c => c.Key).join(', '));
    })
    .catch(e => console.log('R2 ERROR:', e.message));
